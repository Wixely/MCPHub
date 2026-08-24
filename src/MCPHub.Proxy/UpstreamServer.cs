using ModelContextProtocol.Client;

namespace MCPHub.Proxy;

/// <summary>Connection state of one upstream MCP server the proxy aggregates.</summary>
public enum UpstreamState
{
    /// <summary>No live connection (initial state, or after a disconnect).</summary>
    Disconnected,

    /// <summary>A connection attempt is in flight.</summary>
    Connecting,

    /// <summary>Connected; its tools are part of the aggregated catalog.</summary>
    Connected,

    /// <summary>The last connection attempt failed; the registry retries with backoff.</summary>
    Faulted,
}

/// <summary>One upstream MCP server (a running sub-server or a user-added server) the proxy routes to.</summary>
public sealed class UpstreamServer
{
    /// <summary>Short stable slug used to namespace this server's tools, e.g. <c>noteworthy</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Human-friendly name for the proxy status UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Display label for the upstream endpoint, e.g. <c>http://localhost:5711/mcp</c> or <c>stdio: …</c>.</summary>
    public required string Endpoint { get; set; }

    /// <summary>Current connection state.</summary>
    public UpstreamState State { get; set; } = UpstreamState.Disconnected;

    /// <summary>Message of the most recent connection/list failure, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Number of tools this upstream contributed on its last successful list.</summary>
    public int ToolCount { get; set; }

    /// <summary>The live client once connected (owned by the registry).</summary>
    internal McpClient? Client { get; set; }

    /// <summary>Factory used to (re)create the transport when reconnecting.</summary>
    internal Func<IClientTransport>? TransportFactory { get; set; }

    /// <summary>Earliest time a faulted upstream may be retried.</summary>
    internal DateTimeOffset NextRetryAt { get; set; }

    /// <summary>Current backoff delay (doubles on each failure, capped).</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);
}
