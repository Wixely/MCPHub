using System.Net.Http;
using MCPHub.Core.Logging;
using MCPHub.Core.Models;
using Microsoft.Extensions.Logging;
using DiagProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace MCPHub.Core.Process;

/// <summary>
/// Starts/stops managed MCP sub-servers as hidden child processes, pipes their stdout/stderr into the
/// <see cref="ILogStore"/>, and drives <see cref="ServiceRunState"/> via a periodic <c>/healthz</c> probe.
/// </summary>
public interface IServiceProcessHost : IAsyncDisposable
{
    /// <summary>Starts the service hidden (no-op if already running). Transitions to Starting → Running.</summary>
    Task StartAsync(ManagedService service, CancellationToken cancellationToken = default);

    /// <summary>Requests a stop and kills the process tree. Transitions to Stopping → Stopped.</summary>
    Task StopAsync(ManagedService service, CancellationToken cancellationToken = default);

    /// <summary>Stops every running service (used on app shutdown).</summary>
    Task StopAllAsync();

    bool IsRunning(string serviceName);

    /// <summary>Raised whenever a service's run-state changes (on a background thread).</summary>
    event Action<ManagedService>? StateChanged;
}

/// <inheritdoc />
public sealed class ServiceProcessHost : IServiceProcessHost
{
    /// <summary>Name of the configured short-timeout <see cref="HttpClient"/> used for health probes.</summary>
    public const string HealthClientName = "health";

    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NoPortGrace = TimeSpan.FromSeconds(2);

    private readonly ILogStore _logStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ServiceProcessHost> _logger;

    private readonly Dictionary<string, RunningProcess> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _healthLoop;

    public ServiceProcessHost(ILogStore logStore, IHttpClientFactory httpClientFactory, ILogger<ServiceProcessHost> logger)
    {
        _logStore = logStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public event Action<ManagedService>? StateChanged;

    public bool IsRunning(string serviceName)
    {
        lock (_gate)
            return _running.ContainsKey(serviceName);
    }

    public Task StartAsync(ManagedService service, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(service.Catalog.Name))
                return Task.CompletedTask;
        }

        if (!File.Exists(service.ExecutablePath))
        {
            AppendInfo(service, $"Executable not found: {service.ExecutablePath}");
            SetState(service, ServiceRunState.Faulted);
            return Task.CompletedTask;
        }

        // Follow the server's own config for the effective port; fall back to the catalog default.
        service.Port = ServerConfigReader.ReadPort(service.ConfigPath) ?? service.Catalog.DefaultPort;

        var process = new DiagProcess
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = service.ExecutablePath,
                WorkingDirectory = service.InstallFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        var running = new RunningProcess(process, service);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(service, LogStream.Stdout, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(service, LogStream.Stderr, e.Data); };
        process.Exited += (_, _) => OnExited(running);

        try
        {
            SetState(service, ServiceRunState.Starting);
            service.StartedAt = DateTimeOffset.Now;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            service.ProcessId = process.Id;
            AppendInfo(service, $"Started (pid {process.Id}); waiting for health on port {service.Port?.ToString() ?? "?"}…");
        }
        catch (Exception ex)
        {
            AppendInfo(service, "Failed to start: " + ex.Message);
            _logger.LogError(ex, "Failed to start {Service}.", service.Catalog.Name);
            SetState(service, ServiceRunState.Faulted);
            return Task.CompletedTask;
        }

        lock (_gate)
            _running[service.Catalog.Name] = running;

        EnsureHealthLoop();
        return Task.CompletedTask;
    }

    public Task StopAsync(ManagedService service, CancellationToken cancellationToken = default)
    {
        RunningProcess? running;
        lock (_gate)
            _running.TryGetValue(service.Catalog.Name, out running);

        if (running is null)
        {
            SetState(service, ServiceRunState.Stopped);
            return Task.CompletedTask;
        }

        running.StopRequested = true;
        SetState(service, ServiceRunState.Stopping);
        try
        {
            running.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppendInfo(service, "Kill failed: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    public Task StopAllAsync()
    {
        List<RunningProcess> snapshot;
        lock (_gate)
            snapshot = _running.Values.ToList();

        foreach (var running in snapshot)
        {
            running.StopRequested = true;
            try { running.Process.Kill(entireProcessTree: true); }
            catch { /* best effort on shutdown */ }
        }

        return Task.CompletedTask;
    }

    private void OnExited(RunningProcess running)
    {
        var service = running.Service;
        var exitCode = TryGetExitCode(running.Process);

        lock (_gate)
            _running.Remove(service.Catalog.Name);

        service.ProcessId = null;

        if (running.StopRequested)
        {
            AppendInfo(service, $"Stopped (exit code {exitCode}).");
            SetState(service, ServiceRunState.Stopped);
        }
        else
        {
            AppendInfo(service, $"Exited unexpectedly (exit code {exitCode}).");
            SetState(service, ServiceRunState.Faulted);
        }

        try { running.Process.Dispose(); } catch { /* ignore */ }
    }

    private void EnsureHealthLoop()
    {
        lock (_gate)
            _healthLoop ??= Task.Run(HealthLoopAsync);
    }

    private async Task HealthLoopAsync()
    {
        var http = _httpClientFactory.CreateClient(HealthClientName);
        using var timer = new PeriodicTimer(HealthInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                List<RunningProcess> snapshot;
                lock (_gate)
                    snapshot = _running.Values.ToList();

                foreach (var running in snapshot)
                {
                    if (!running.Process.HasExited)
                        await ProbeHealthAsync(running.Service, http);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProbeHealthAsync(ManagedService service, HttpClient http)
    {
        // No known port → can't probe; treat as Running once it has survived a short grace period.
        if (service.HealthUrl is null)
        {
            if (service.RunState == ServiceRunState.Starting &&
                service.StartedAt is { } started && DateTimeOffset.Now - started > NoPortGrace)
                SetState(service, ServiceRunState.Running);
            return;
        }

        try
        {
            using var response = await http.GetAsync(service.HealthUrl, _shutdown.Token);
            if (response.IsSuccessStatusCode)
            {
                if (service.RunState is ServiceRunState.Starting or ServiceRunState.Unhealthy)
                    SetState(service, ServiceRunState.Running);
            }
            else if (service.RunState == ServiceRunState.Running)
            {
                SetState(service, ServiceRunState.Unhealthy);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // shutting down
        }
        catch
        {
            // Connection refused/timeout. While Starting the server may still be booting; once it has
            // been Running, a failure means it went Unhealthy.
            if (service.RunState == ServiceRunState.Running)
                SetState(service, ServiceRunState.Unhealthy);
        }
    }

    private void Append(ManagedService service, LogStream stream, string text)
        => _logStore.Append(service.Catalog.Name, new LogLine(DateTimeOffset.Now, stream, text));

    private void AppendInfo(ManagedService service, string text)
        => _logStore.Append(service.Catalog.Name, new LogLine(DateTimeOffset.Now, LogStream.Info, text));

    private void SetState(ManagedService service, ServiceRunState state)
    {
        service.RunState = state;
        StateChanged?.Invoke(service);
    }

    private static int TryGetExitCode(DiagProcess process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await StopAllAsync();
        if (_healthLoop is not null)
        {
            try { await _healthLoop; } catch { /* ignore */ }
        }
        _shutdown.Dispose();
    }

    private sealed class RunningProcess(DiagProcess process, ManagedService service)
    {
        public DiagProcess Process { get; } = process;
        public ManagedService Service { get; } = service;
        public bool StopRequested { get; set; }
    }
}
