namespace MCPHub.Processes;

/// <summary>Lifecycle state of a supervised process.</summary>
public enum ProcessRunState
{
    /// <summary>Not running.</summary>
    Stopped,

    /// <summary>Process started; waiting for its health endpoint to report ready.</summary>
    Starting,

    /// <summary>Process running and health checks passing (or no health URL and past the grace period).</summary>
    Running,

    /// <summary>Process alive but health checks failing.</summary>
    Unhealthy,

    /// <summary>Stop requested; process is shutting down.</summary>
    Stopping,

    /// <summary>Process exited unexpectedly (e.g. crashed during startup), or could not be started.</summary>
    Faulted,
}

/// <summary>Which stream a captured output line came from.</summary>
public enum ProcessOutputStream
{
    /// <summary>The child process's standard output.</summary>
    Stdout,

    /// <summary>The child process's standard error.</summary>
    Stderr,

    /// <summary>A host-generated lifecycle message (started, stopped, health change…).</summary>
    Info,
}

/// <summary>Everything needed to launch and supervise one process. A plain options object.</summary>
public sealed class ProcessSpec
{
    /// <summary>Unique key the process is tracked, stopped and reported under.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Executable to launch. A rooted path must exist on disk; a bare name is resolved via
    /// <c>PATH</c> at spawn time.
    /// </summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Working directory for the child (defaults to the executable's directory).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Command-line arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Extra environment variables for the child (merged over the inherited environment).</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Health endpoint to probe (e.g. <c>http://127.0.0.1:5710/healthz</c>). When
    /// <see langword="null"/>, the process is considered <see cref="ProcessRunState.Running"/> once
    /// it survives the host's grace period.
    /// </summary>
    public Uri? HealthUrl { get; init; }
}

/// <summary>Tuning for <see cref="ProcessHost"/>. The defaults match MCPHub's desktop behavior.</summary>
public sealed class ProcessHostOptions
{
    /// <summary>Interval between health probes.</summary>
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a process without a health URL must survive before it counts as running.</summary>
    public TimeSpan NoHealthGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Supplies the <see cref="HttpClient"/> used for health probes (called once, when the first
    /// process starts). Defaults to a plain client with a 3-second timeout; supply your own to use
    /// a pooled/named client.
    /// </summary>
    public Func<HttpClient>? HealthClientFactory { get; init; }
}

/// <summary>A state transition of a supervised process.</summary>
/// <param name="Name">The process's <see cref="ProcessSpec.Name"/>.</param>
/// <param name="State">The new state.</param>
/// <param name="ProcessId">OS process id while alive; <see langword="null"/> once exited (or never started).</param>
public readonly record struct ProcessStateChange(string Name, ProcessRunState State, int? ProcessId);

/// <summary>One captured output line from a supervised process.</summary>
/// <param name="Name">The process's <see cref="ProcessSpec.Name"/>.</param>
/// <param name="Stream">Which stream the line came from.</param>
/// <param name="Text">The line text.</param>
/// <param name="Timestamp">Local time the line was captured.</param>
public readonly record struct ProcessOutputLine(string Name, ProcessOutputStream Stream, string Text, DateTimeOffset Timestamp);
