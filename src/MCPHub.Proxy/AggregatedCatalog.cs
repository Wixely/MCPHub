using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MCPHub.Proxy;

/// <summary>
/// Where an exposed (namespaced) tool routes to: the owning client, its original tool name, and the
/// upstream's key (the namespace prefix — carried so authorization can grant per upstream server).
/// </summary>
public sealed record ToolRoute(McpClient Client, string OriginalName, string ServerKey);

/// <summary>
/// Immutable snapshot of the aggregated tool set across all connected upstreams: the namespaced tools
/// to advertise plus an O(1) map from exposed tool name back to the owning client + original name.
/// Swapped atomically so handler reads never block.
/// </summary>
public sealed class AggregatedCatalog
{
    /// <summary>The empty catalog (no upstreams connected).</summary>
    public static readonly AggregatedCatalog Empty =
        new([], new Dictionary<string, ToolRoute>(StringComparer.Ordinal));

    /// <summary>Creates a snapshot from the advertised tools and their routes.</summary>
    public AggregatedCatalog(IReadOnlyList<Tool> tools, IReadOnlyDictionary<string, ToolRoute> routes)
    {
        Tools = tools;
        Routes = routes;
    }

    /// <summary>Namespaced tools to advertise, e.g. <c>noteworthy__list_notes</c>.</summary>
    public IReadOnlyList<Tool> Tools { get; }

    /// <summary>Maps an exposed tool name to its owning client + original name.</summary>
    public IReadOnlyDictionary<string, ToolRoute> Routes { get; }
}
