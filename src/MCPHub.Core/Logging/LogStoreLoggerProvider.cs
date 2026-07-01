using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCPHub.Core.Logging;

/// <summary>
/// Bridges MCPHub's own proxy-related <see cref="ILogger"/> output into the <see cref="ILogStore"/> so
/// it shows on the Logs page under <see cref="ProxyLogKey"/>. Only "Proxy" categories are captured
/// (the aggregator, coordinator and host); every other category gets a no-op logger.
/// </summary>
public sealed class LogStoreLoggerProvider : ILoggerProvider
{
    /// <summary>Log-store key and Logs-page label for the aggregated MCPHub proxy log.</summary>
    public const string ProxyLogKey = "MCPHub Proxy";

    private readonly ILogStore _logStore;

    public LogStoreLoggerProvider(ILogStore logStore) => _logStore = logStore;

    public ILogger CreateLogger(string categoryName)
        => categoryName.Contains("Proxy", StringComparison.OrdinalIgnoreCase)
            ? new StoreLogger(_logStore)
            : NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class StoreLogger(ILogStore store) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            if (exception is not null)
                message += " — " + exception.Message;

            var stream = logLevel >= LogLevel.Warning ? LogStream.Stderr : LogStream.Info;
            store.Append(ProxyLogKey, new LogLine(DateTimeOffset.Now, stream, message));
        }
    }
}
