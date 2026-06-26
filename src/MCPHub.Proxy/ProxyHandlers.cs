using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MCPHub.Proxy;

/// <summary>
/// Dynamic MCP request handlers for the aggregator: advertise the namespaced union of upstream tools,
/// and route a tool call to its owning upstream by splitting the exposed name on the namespace separator.
/// </summary>
public sealed class ProxyHandlers
{
    private readonly IUpstreamRegistry _registry;

    public ProxyHandlers(IUpstreamRegistry registry) => _registry = registry;

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
    {
        var catalog = _registry.Catalog;
        return ValueTask.FromResult(new ListToolsResult { Tools = catalog.Tools.ToList() });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var exposedName = context.Params?.Name;
        if (string.IsNullOrEmpty(exposedName))
            return Error("No tool name supplied.");

        if (!_registry.Catalog.Routes.TryGetValue(exposedName, out var route))
            return Error($"Unknown tool '{exposedName}'. The owning service may have stopped — re-list tools.");

        try
        {
            return await route.Client.CallToolAsync(
                new CallToolRequestParams { Name = route.OriginalName, Arguments = context.Params!.Arguments },
                cancellationToken);
        }
        catch (Exception ex)
        {
            return Error($"Upstream call to '{route.OriginalName}' failed: {ex.Message}");
        }
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
