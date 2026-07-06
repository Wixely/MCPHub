using System.Net.Http;
using MCPHub.Core.Logging;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using Microsoft.Extensions.Logging;
using DiagProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace MCPHub.Core.Agent;

/// <summary>
/// Runs DaggerAgent in its selectable modes: <see cref="AgentRunMode.Web"/> / <see cref="AgentRunMode.Jobs"/>
/// launch <c>dagger serve</c> as a hidden managed child process (health-probed on <c>/agent/healthz</c>,
/// output piped into the log store, killed with MCPHub via a Job Object), while
/// <see cref="AgentRunMode.Cli"/> launches an independent interactive REPL in its own console window.
/// Web and Jobs share the serve port, so only one runs at a time.
/// </summary>
public interface IAgentProcessHost : IAsyncDisposable
{
    /// <summary>The agent's shared run/install state.</summary>
    ManagedService Agent { get; }

    /// <summary>The serve mode currently running (Web/Jobs), or <see langword="null"/> if not serving.</summary>
    AgentRunMode? ServingMode { get; }

    bool IsServing { get; }

    /// <summary>
    /// Starts a serve mode (Web/Jobs); stops any other serve mode first. When
    /// <paramref name="openBrowserWhenReady"/> is set, the agent UI opens in the browser once the server
    /// passes its first health check — not before. When <paramref name="bindAllInterfaces"/> is set the
    /// server binds to <c>0.0.0.0</c> (LAN-reachable) instead of loopback; MCPHub still probes and opens
    /// the UI on <c>127.0.0.1</c> regardless. Cli is delegated to <see cref="LaunchCli"/>.
    /// </summary>
    Task StartServeAsync(AgentRunMode mode, bool openBrowserWhenReady = false, bool bindAllInterfaces = false, CancellationToken cancellationToken = default);

    /// <summary>Stops the running serve process (if any).</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Launches the interactive REPL in its own console window (independent of MCPHub).</summary>
    void LaunchCli();

    /// <summary>Raised whenever the serve run-state changes (on a background thread).</summary>
    event Action? StateChanged;
}

/// <inheritdoc />
public sealed class AgentProcessHost : IAgentProcessHost
{
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(2);

    private readonly AgentContext _context;
    private readonly ILogStore _logStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AgentProcessHost> _logger;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly WindowsJobObject? _jobObject;

    private DiagProcess? _serve;
    private AgentRunMode? _pendingBrowserOpen;
    private bool _stopRequested;
    private Task? _healthLoop;

    public AgentProcessHost(AgentContext context, ILogStore logStore, IHttpClientFactory httpClientFactory,
        ILogger<AgentProcessHost> logger)
    {
        _context = context;
        _logStore = logStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        if (OperatingSystem.IsWindows())
            _jobObject = new WindowsJobObject();
    }

    public ManagedService Agent => _context.Agent;

    public AgentRunMode? ServingMode { get; private set; }

    public bool IsServing
    {
        get
        {
            lock (_gate)
            {
                try { return _serve is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    public event Action? StateChanged;

    public async Task StartServeAsync(AgentRunMode mode, bool openBrowserWhenReady = false, bool bindAllInterfaces = false, CancellationToken cancellationToken = default)
    {
        if (mode == AgentRunMode.Cli)
        {
            LaunchCli();
            return;
        }

        // Web and Jobs share the serve port — stop a different running mode first.
        if (IsServing)
        {
            if (ServingMode == mode)
            {
                if (openBrowserWhenReady)
                    OpenInBrowser(mode); // already serving and healthy — open immediately
                return;
            }
            await StopAsync(cancellationToken);
        }

        var agent = Agent;
        if (!File.Exists(agent.ExecutablePath))
        {
            AppendInfo($"Executable not found: {agent.ExecutablePath}");
            SetState(ServiceRunState.Faulted);
            return;
        }

        agent.Port = DaggerAgent.DefaultServePort;

        var psi = new ProcessStartInfo
        {
            FileName = agent.ExecutablePath,
            WorkingDirectory = agent.InstallFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("serve");
        psi.Environment[DaggerAgent.TriggersEnabledEnv] = mode == AgentRunMode.Jobs ? "true" : "false";
        // Bind all interfaces (LAN-reachable) when asked; otherwise let the agent use its own default
        // (loopback). Health probe and browser still target 127.0.0.1 either way.
        if (bindAllInterfaces)
            psi.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{agent.Port}";

        var process = new DiagProcess { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(LogStream.Stdout, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(LogStream.Stderr, e.Data); };
        process.Exited += (_, _) => OnExited(process);

        try
        {
            _stopRequested = false;
            SetState(ServiceRunState.Starting);
            agent.StartedAt = DateTimeOffset.Now;
            process.Start();
            if (OperatingSystem.IsWindows())
                _jobObject?.AssignProcess(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            agent.ProcessId = process.Id;
            lock (_gate)
            {
                _serve = process;
                ServingMode = mode;
                _pendingBrowserOpen = openBrowserWhenReady ? mode : null;
            }
            var bindNote = bindAllInterfaces ? " on 0.0.0.0 (all interfaces)" : "";
            AppendInfo($"Started 'dagger serve' ({mode}, pid {process.Id}){bindNote}; waiting for health on :{agent.Port}…");
        }
        catch (Exception ex)
        {
            AppendInfo("Failed to start: " + ex.Message);
            _logger.LogError(ex, "Failed to start DaggerAgent serve.");
            SetState(ServiceRunState.Faulted);
            return;
        }

        EnsureHealthLoop();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        DiagProcess? process;
        lock (_gate)
            process = _serve;

        if (process is null)
        {
            SetState(ServiceRunState.Stopped);
            return Task.CompletedTask;
        }

        _stopRequested = true;
        SetState(ServiceRunState.Stopping);
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { AppendInfo("Kill failed: " + ex.Message); }

        return Task.CompletedTask;
    }

    public void LaunchCli()
    {
        var agent = Agent;
        if (!File.Exists(agent.ExecutablePath))
        {
            AppendInfo($"Executable not found: {agent.ExecutablePath}");
            return;
        }

        try
        {
            // UseShellExecute gives the console app its own window; not tracked or killed with MCPHub.
            DiagProcess.Start(new ProcessStartInfo
            {
                FileName = agent.ExecutablePath,
                WorkingDirectory = agent.InstallFolder,
                UseShellExecute = true,
            });
            AppendInfo("Launched CLI (interactive REPL) in a new console window.");
        }
        catch (Exception ex)
        {
            AppendInfo("Failed to launch CLI: " + ex.Message);
            _logger.LogError(ex, "Failed to launch DaggerAgent CLI.");
        }
    }

    private void OpenPendingBrowser()
    {
        AgentRunMode? mode;
        lock (_gate)
        {
            mode = _pendingBrowserOpen;
            _pendingBrowserOpen = null;
        }
        if (mode is { } m)
            OpenInBrowser(m);
    }

    private void OpenInBrowser(AgentRunMode mode)
    {
        var port = Agent.Port ?? DaggerAgent.DefaultServePort;
        var url = $"http://127.0.0.1:{port}{DaggerAgent.UiPath}";
        try
        {
            AppendInfo($"Opening the agent UI at {url} …");
            DiagProcess.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendInfo("Failed to open browser: " + ex.Message);
        }
    }

    private void OnExited(DiagProcess process)
    {
        var exit = TryGetExitCode(process);

        lock (_gate)
        {
            if (ReferenceEquals(_serve, process))
            {
                _serve = null;
                ServingMode = null;
                _pendingBrowserOpen = null;
            }
        }

        Agent.ProcessId = null;

        if (_stopRequested)
        {
            AppendInfo($"Stopped (exit code {exit}).");
            SetState(ServiceRunState.Stopped);
        }
        else
        {
            AppendInfo($"Exited unexpectedly (exit code {exit}).");
            SetState(ServiceRunState.Faulted);
        }

        try { process.Dispose(); } catch { /* ignore */ }
    }

    private void EnsureHealthLoop()
    {
        lock (_gate)
            _healthLoop ??= Task.Run(HealthLoopAsync);
    }

    private async Task HealthLoopAsync()
    {
        var http = _httpClientFactory.CreateClient(ServiceProcessHost.HealthClientName);
        using var timer = new PeriodicTimer(HealthInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                DiagProcess? process;
                lock (_gate)
                    process = _serve;

                if (process is null || process.HasExited)
                    continue;

                await ProbeAsync(http);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProbeAsync(HttpClient http)
    {
        var agent = Agent;
        var port = agent.Port ?? DaggerAgent.DefaultServePort;
        var url = $"http://127.0.0.1:{port}{DaggerAgent.HealthPath}";

        try
        {
            using var response = await http.GetAsync(url, _shutdown.Token);
            if (response.IsSuccessStatusCode)
            {
                if (agent.RunState is ServiceRunState.Starting or ServiceRunState.Unhealthy)
                {
                    SetState(ServiceRunState.Running);
                    OpenPendingBrowser(); // only now that it's actually serving
                }
            }
            else if (agent.RunState == ServiceRunState.Running)
            {
                SetState(ServiceRunState.Unhealthy);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // shutting down
        }
        catch
        {
            if (agent.RunState == ServiceRunState.Running)
                SetState(ServiceRunState.Unhealthy);
        }
    }

    private void Append(LogStream stream, string text)
        => _logStore.Append(DaggerAgent.LogKey, new LogLine(DateTimeOffset.Now, stream, text));

    private void AppendInfo(string text)
        => _logStore.Append(DaggerAgent.LogKey, new LogLine(DateTimeOffset.Now, LogStream.Info, text));

    private void SetState(ServiceRunState state)
    {
        Agent.RunState = state;
        StateChanged?.Invoke();
    }

    private static int TryGetExitCode(DiagProcess process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await StopAsync();
        if (_healthLoop is not null)
        {
            try { await _healthLoop; } catch { /* ignore */ }
        }
        if (OperatingSystem.IsWindows())
            _jobObject?.Dispose();
        _shutdown.Dispose();
    }
}
