using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace MCPHub.Proxy;

/// <summary>
/// A set of tools implemented inside the proxy process rather than by an upstream MCP server —
/// MCPHub's own recipes knowledge base, for instance. Providers are advertised and routed exactly
/// like upstreams: their tools are namespaced as <c>{Key}__{tool}</c>, filtered per tenant through
/// <see cref="IToolAuthorization"/> with <see cref="Key"/> as the server key, and every call is
/// audited. A provider's tool set is read once when <see cref="ProxyHandlers"/> is constructed,
/// so it must be fixed for the provider's lifetime.
/// </summary>
public interface ILocalToolProvider
{
    /// <summary>Short stable slug used to namespace the provider's tools, e.g. <c>recipes</c>.</summary>
    string Key { get; }

    /// <summary>Human-friendly label prefixed onto each tool description, e.g. <c>[Recipes]</c>.</summary>
    string DisplayName { get; }

    /// <summary>The tools offered, with their un-namespaced names (e.g. <c>list</c>).</summary>
    IReadOnlyList<Tool> Tools { get; }

    /// <summary>Executes <paramref name="toolName"/> (un-namespaced) with the caller's arguments.</summary>
    ValueTask<CallToolResult> CallAsync(string toolName, IReadOnlyDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken);
}
