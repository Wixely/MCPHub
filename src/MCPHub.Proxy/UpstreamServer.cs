using ModelContextProtocol.Client;

namespace MCPHub.Proxy;

/// <summary>Connection state of one upstream MCP server the proxy aggregates.</summary>
public enum UpstreamState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

/// <summary>One upstream MCP server (a running sub-server or a user-added server) the proxy routes to.</summary>
public sealed class UpstreamServer
{
    /// <summary>Short stable slug used to namespace this server's tools, e.g. <c>noteworthy</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Human-friendly name for the proxy status UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The upstream MCP HTTP endpoint, e.g. <c>http://localhost:5711/mcp</c>.</summary>
    public required Uri Endpoint { get; set; }

    public UpstreamState State { get; set; } = UpstreamState.Disconnected;

    public string? LastError { get; set; }

    public int ToolCount { get; set; }

    /// <summary>The live client once connected (owned by the registry).</summary>
    internal McpClient? Client { get; set; }
}
