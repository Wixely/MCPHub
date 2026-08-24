using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DiagProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace MCPHub.Processes;

/// <summary>
/// Starts/stops supervised child processes described by <see cref="ProcessSpec"/>s, captures their
/// stdout/stderr, and drives <see cref="ProcessRunState"/> via a periodic health probe. On Windows,
/// children are assigned to a kill-on-close job object so they die with the host process even on a
/// crash or force-kill.
/// </summary>
public interface IProcessHost : IAsyncDisposable
{
    /// <summary>Starts the process hidden (no-op if already running). Transitions to Starting → Running.</summary>
    Task StartAsync(ProcessSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Requests a stop and kills the process tree. Transitions to Stopping → Stopped.</summary>
    Task StopAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Stops every running process (used on host shutdown).</summary>
    Task StopAllAsync();

    /// <summary>Whether a process with this <see cref="ProcessSpec.Name"/> is currently tracked as running.</summary>
    bool IsRunning(string name);

    /// <summary>Raised whenever a process's run-state changes (on a background thread).</summary>
    event Action<ProcessStateChange>? StateChanged;

    /// <summary>Raised for every captured output line (on a background thread).</summary>
    event Action<ProcessOutputLine>? OutputReceived;
}

/// <inheritdoc />
public sealed class ProcessHost : IProcessHost
{
    private readonly ProcessHostOptions _options;
    private readonly ILogger _logger;

    private readonly Dictionary<string, RunningProcess> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly WindowsJobObject? _jobObject;
    private HttpClient? _healthClient;
    private Task? _healthLoop;

    /// <summary>Creates a host; on Windows a kill-on-close job object is created immediately.</summary>
    public ProcessHost(ProcessHostOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new ProcessHostOptions();
        _logger = logger ?? NullLogger.Instance;

        // Children assigned to this job die with the host even on a crash/force-kill.
        if (OperatingSystem.IsWindows())
            _jobObject = new WindowsJobObject();
    }

    /// <inheritdoc />
    public event Action<ProcessStateChange>? StateChanged;

    /// <inheritdoc />
    public event Action<ProcessOutputLine>? OutputReceived;

    /// <inheritdoc />
    public bool IsRunning(string name)
    {
        lock (_gate)
            return _running.ContainsKey(name);
    }

    /// <inheritdoc />
    public Task StartAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        lock (_gate)
        {
            if (_running.ContainsKey(spec.Name))
                return Task.CompletedTask;
        }

        if (Path.IsPathRooted(spec.ExecutablePath) && !File.Exists(spec.ExecutablePath))
        {
            AppendInfo(spec.Name, $"Executable not found: {spec.ExecutablePath}");
            SetState(spec.Name, ProcessRunState.Faulted, processId: null);
            return Task.CompletedTask;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            WorkingDirectory = spec.WorkingDirectory ?? Path.GetDirectoryName(spec.ExecutablePath) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in spec.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in spec.EnvironmentVariables)
            startInfo.Environment[key] = value;

        var process = new DiagProcess { StartInfo = startInfo, EnableRaisingEvents = true };
        var running = new RunningProcess(process, spec);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(spec.Name, ProcessOutputStream.Stdout, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(spec.Name, ProcessOutputStream.Stderr, e.Data); };
        process.Exited += (_, _) => OnExited(running);

        try
        {
            SetState(spec.Name, ProcessRunState.Starting, processId: null);
            running.StartedAt = DateTimeOffset.Now;
            process.Start();
            if (OperatingSystem.IsWindows())
                _jobObject?.AssignProcess(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendInfo(spec.Name, $"Started (pid {process.Id}); waiting for health on port {spec.HealthUrl?.Port.ToString() ?? "?"}…");
        }
        catch (Exception ex)
        {
            AppendInfo(spec.Name, "Failed to start: " + ex.Message);
            _logger.LogError(ex, "Failed to start {Process}.", spec.Name);
            SetState(spec.Name, ProcessRunState.Faulted, processId: null);
            return Task.CompletedTask;
        }

        lock (_gate)
            _running[spec.Name] = running;

        EnsureHealthLoop();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(string name, CancellationToken cancellationToken = default)
    {
        RunningProcess? running;
        lock (_gate)
            _running.TryGetValue(name, out running);

        if (running is null)
        {
            SetState(name, ProcessRunState.Stopped, processId: null);
            return Task.CompletedTask;
        }

        running.StopRequested = true;
        SetState(name, ProcessRunState.Stopping, TryGetId(running.Process));
        try
        {
            running.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppendInfo(name, "Kill failed: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
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
        var name = running.Spec.Name;
        var exitCode = TryGetExitCode(running.Process);

        lock (_gate)
            _running.Remove(name);

        if (running.StopRequested)
        {
            AppendInfo(name, $"Stopped (exit code {exitCode}).");
            SetState(name, ProcessRunState.Stopped, processId: null);
        }
        else
        {
            AppendInfo(name, $"Exited unexpectedly (exit code {exitCode}).");
            SetState(name, ProcessRunState.Faulted, processId: null);
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
        var http = _healthClient ??= _options.HealthClientFactory?.Invoke()
            ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var timer = new PeriodicTimer(_options.HealthInterval);

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
                        await ProbeHealthAsync(running, http);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProbeHealthAsync(RunningProcess running, HttpClient http)
    {
        var name = running.Spec.Name;

        // No health URL → can't probe; treat as Running once it has survived a short grace period.
        if (running.Spec.HealthUrl is null)
        {
            if (running.State == ProcessRunState.Starting &&
                DateTimeOffset.Now - running.StartedAt > _options.NoHealthGrace)
                SetState(name, ProcessRunState.Running, TryGetId(running.Process));
            return;
        }

        try
        {
            using var response = await http.GetAsync(running.Spec.HealthUrl, _shutdown.Token);
            if (response.IsSuccessStatusCode)
            {
                if (running.State is ProcessRunState.Starting or ProcessRunState.Unhealthy)
                    SetState(name, ProcessRunState.Running, TryGetId(running.Process));
            }
            else if (running.State == ProcessRunState.Running)
            {
                SetState(name, ProcessRunState.Unhealthy, TryGetId(running.Process));
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
            if (running.State == ProcessRunState.Running)
                SetState(name, ProcessRunState.Unhealthy, TryGetId(running.Process));
        }
    }

    private void Append(string name, ProcessOutputStream stream, string text)
        => OutputReceived?.Invoke(new ProcessOutputLine(name, stream, text, DateTimeOffset.Now));

    private void AppendInfo(string name, string text)
        => Append(name, ProcessOutputStream.Info, text);

    private void SetState(string name, ProcessRunState state, int? processId)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(name, out var running))
                running.State = state;
        }

        StateChanged?.Invoke(new ProcessStateChange(name, state, processId));
    }

    private static int? TryGetId(DiagProcess process)
    {
        try { return process.HasExited ? null : process.Id; }
        catch { return null; }
    }

    private static int TryGetExitCode(DiagProcess process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    /// <summary>Stops every process, ends the health loop, and closes the job object (killing survivors).</summary>
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await StopAllAsync();
        if (_healthLoop is not null)
        {
            try { await _healthLoop; } catch { /* ignore */ }
        }
        if (OperatingSystem.IsWindows())
            _jobObject?.Dispose();
        _healthClient?.Dispose();
        _shutdown.Dispose();
    }

    private sealed class RunningProcess(DiagProcess process, ProcessSpec spec)
    {
        public DiagProcess Process { get; } = process;
        public ProcessSpec Spec { get; } = spec;
        public bool StopRequested { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public ProcessRunState State { get; set; } = ProcessRunState.Starting;
    }
}
