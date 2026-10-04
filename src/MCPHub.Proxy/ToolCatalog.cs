namespace MCPHub.Proxy;

/// <summary>Where a group of tools comes from, which is the difference an operator cares about.</summary>
public enum ToolGroupKind
{
    /// <summary>An MCP service this hub proxies. Its tools exist only while it is connected.</summary>
    Service,

    /// <summary>MCPHub's own tools — recipes, service management, users and permissions.</summary>
    Hub,
}

/// <summary>
/// One server's worth of tools, named as the proxy advertises them.
/// </summary>
/// <param name="Key">The namespace prefix, so <c>Key + "__*"</c> is the grant covering the whole group.</param>
/// <param name="DisplayName">What the service calls itself, for a list a person reads.</param>
/// <param name="Kind">Whether these are a proxied service's tools or the hub's own.</param>
/// <param name="IsAvailable">Whether any tool is currently on offer, i.e. the service is connected.</param>
/// <param name="Tools">Exposed tool names. Empty for a service that is registered but not connected —
/// a real state, and one a tool picker has to show rather than pretend the service has nothing.</param>
public sealed record ToolGroup(
    string Key, string DisplayName, ToolGroupKind Kind, bool IsAvailable, IReadOnlyList<string> Tools);

/// <summary>
/// What tools exist right now, grouped by the server that offers them.
///
/// <para>Read by anything that has to <em>offer</em> tools rather than serve them — the Permissions
/// page's picker above all, where a list of checkboxes beats typing <c>kodi__play_pause</c> correctly.
/// Deliberately a snapshot taken on demand: tools appear and disappear as services connect, and a
/// picker that cached them would offer things that are no longer there.</para>
/// </summary>
public interface IToolCatalog
{
    /// <summary>Every group, services first, each in a stable order.</summary>
    IReadOnlyList<ToolGroup> Groups { get; }
}

/// <inheritdoc />
public sealed class ToolCatalog : IToolCatalog
{
    private readonly IUpstreamRegistry _registry;
    private readonly IReadOnlyList<ILocalToolProvider> _local;

    /// <param name="registry">The proxied services and their live catalogue.</param>
    /// <param name="local">The hub's own tool providers, which need no connection to be listed.</param>
    public ToolCatalog(IUpstreamRegistry registry, IEnumerable<ILocalToolProvider> local)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(local);
        _registry = registry;
        _local = [.. local];
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolGroup> Groups
    {
        get
        {
            var catalog = _registry.Catalog;
            var byServer = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (exposedName, route) in catalog.Routes)
            {
                if (!byServer.TryGetValue(route.ServerKey, out var tools))
                {
                    byServer[route.ServerKey] = tools = [];
                }

                tools.Add(exposedName);
            }

            var groups = new List<ToolGroup>();
            foreach (var upstream in _registry.Upstreams.OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var tools = byServer.GetValueOrDefault(upstream.Key) ?? [];
                tools.Sort(StringComparer.Ordinal);
                groups.Add(new ToolGroup(
                    upstream.Key,
                    upstream.DisplayName,
                    ToolGroupKind.Service,
                    IsAvailable: tools.Count > 0,
                    tools));
            }

            // A service whose tools are in the catalogue but whose registration has gone is still worth
            // listing: its tools are callable, so they are grantable.
            foreach (var (key, tools) in byServer.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (groups.Any(g => string.Equals(g.Key, key, StringComparison.Ordinal)))
                {
                    continue;
                }

                tools.Sort(StringComparer.Ordinal);
                groups.Add(new ToolGroup(key, key, ToolGroupKind.Service, IsAvailable: true, tools));
            }

            foreach (var provider in _local.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                groups.Add(new ToolGroup(
                    provider.Key,
                    provider.DisplayName,
                    ToolGroupKind.Hub,
                    IsAvailable: true,
                    [.. provider.Tools
                        .Select(t => provider.Key + ProxyConstants.NamespaceSeparator + t.Name)
                        .OrderBy(n => n, StringComparer.Ordinal)]));
            }

            return groups;
        }
    }
}
