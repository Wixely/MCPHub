namespace MCPHub.Core.Logging;

/// <summary>Which stream a captured log line came from.</summary>
public enum LogStream
{
    /// <summary>The child process's standard output.</summary>
    Stdout,

    /// <summary>The child process's standard error.</summary>
    Stderr,

    /// <summary>An MCPHub-generated lifecycle message (started, stopped, health change…).</summary>
    Info,
}

/// <summary>A single captured log line.</summary>
public sealed record LogLine(DateTimeOffset Timestamp, LogStream Stream, string Text);

/// <summary>
/// Thread-safe, per-service bounded log buffer. Output-capture callbacks (on thread-pool threads)
/// append here; the UI reads a <see cref="Snapshot"/> and subscribes to <see cref="LineAppended"/>.
/// </summary>
public interface ILogStore
{
    /// <summary>Maximum lines retained per service before the oldest are dropped.</summary>
    int Capacity { get; }

    /// <summary>Service keys that currently have at least one buffered line (i.e. produced output this session).</summary>
    IReadOnlyCollection<string> Services { get; }

    void Append(string service, LogLine line);

    /// <summary>Returns a point-in-time copy of a service's buffered lines (oldest first).</summary>
    IReadOnlyList<LogLine> Snapshot(string service);

    void Clear(string service);

    /// <summary>Raised after each append, with the service name and the new line.</summary>
    event Action<string, LogLine>? LineAppended;
}

/// <inheritdoc />
public sealed class LogStore : ILogStore
{
    private readonly Dictionary<string, Queue<LogLine>> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public LogStore(int capacity = 5000) => Capacity = capacity;

    public int Capacity { get; }

    public IReadOnlyCollection<string> Services
    {
        get
        {
            lock (_gate)
                return [.. _buffers.Keys];
        }
    }

    public event Action<string, LogLine>? LineAppended;

    public void Append(string service, LogLine line)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(service, out var queue))
                _buffers[service] = queue = new Queue<LogLine>(Capacity);

            queue.Enqueue(line);
            while (queue.Count > Capacity)
                queue.Dequeue();
        }

        LineAppended?.Invoke(service, line);
    }

    public IReadOnlyList<LogLine> Snapshot(string service)
    {
        lock (_gate)
        {
            return _buffers.TryGetValue(service, out var queue) ? queue.ToArray() : [];
        }
    }

    public void Clear(string service)
    {
        lock (_gate)
        {
            _buffers.Remove(service);
        }
    }
}
