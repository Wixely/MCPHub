using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MCPHub.Proxy;

/// <summary>
/// Owns the pool of upstream MCP client connections and the aggregated, namespaced tool catalog.
/// Connecting/disconnecting an upstream re-enumerates tools and rebuilds the catalog snapshot.
/// </summary>
public interface IUpstreamRegistry
{
    IReadOnlyCollection<UpstreamServer> Upstreams { get; }

    /// <summary>Current aggregated catalog snapshot (atomic).</summary>
    AggregatedCatalog Catalog { get; }

    /// <summary>Raised after the catalog changes (an upstream connected/disconnected/re-listed).</summary>
    event Action? CatalogChanged;

    Task ConnectAsync(string key, string displayName, Uri endpoint, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string key, CancellationToken cancellationToken = default);
    Task DisconnectAllAsync();
}

/// <inheritdoc />
public sealed class UpstreamRegistry : IUpstreamRegistry
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<UpstreamRegistry> _logger;
    private readonly ConcurrentDictionary<string, UpstreamServer> _upstreams = new(StringComparer.OrdinalIgnoreCase);
    private volatile AggregatedCatalog _catalog = AggregatedCatalog.Empty;

    public UpstreamRegistry(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<UpstreamRegistry>();
    }

    public IReadOnlyCollection<UpstreamServer> Upstreams => _upstreams.Values.ToArray();

    public AggregatedCatalog Catalog => _catalog;

    public event Action? CatalogChanged;

    public async Task ConnectAsync(string key, string displayName, Uri endpoint, CancellationToken cancellationToken = default)
    {
        var upstream = _upstreams.GetOrAdd(key, _ => new UpstreamServer { Key = key, DisplayName = displayName, Endpoint = endpoint });
        upstream.Endpoint = endpoint;
        upstream.State = UpstreamState.Connecting;
        upstream.LastError = null;

        try
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.AutoDetect },
                _loggerFactory);

            var client = await McpClient.CreateAsync(transport, clientOptions: null, _loggerFactory, cancellationToken);

            await DisposeClientAsync(upstream); // replace any prior client
            upstream.Client = client;
            upstream.State = UpstreamState.Connected;
            _logger.LogInformation("Connected upstream {Key} at {Endpoint}.", key, endpoint);
            await RebuildAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            upstream.State = UpstreamState.Faulted;
            upstream.LastError = ex.Message;
            _logger.LogWarning(ex, "Failed to connect upstream {Key} at {Endpoint}.", key, endpoint);
            await RebuildAsync(cancellationToken);
        }
    }

    public async Task DisconnectAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_upstreams.TryRemove(key, out var upstream))
        {
            await DisposeClientAsync(upstream);
            await RebuildAsync(cancellationToken);
        }
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var upstream in _upstreams.Values)
            await DisposeClientAsync(upstream);
        _upstreams.Clear();
        _catalog = AggregatedCatalog.Empty;
        CatalogChanged?.Invoke();
    }

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var tools = new List<Tool>();
        var routes = new Dictionary<string, ToolRoute>(StringComparer.Ordinal);

        foreach (var upstream in _upstreams.Values)
        {
            if (upstream.State != UpstreamState.Connected || upstream.Client is null)
                continue;

            try
            {
                var result = await upstream.Client.ListToolsAsync(new ListToolsRequestParams(), cancellationToken);
                upstream.ToolCount = result.Tools.Count;

                foreach (var tool in result.Tools)
                {
                    var exposedName = upstream.Key + ProxyConstants.NamespaceSeparator + tool.Name;
                    tools.Add(NamespaceTool(tool, exposedName, upstream.DisplayName));
                    routes[exposedName] = new ToolRoute(upstream.Client, tool.Name);
                }
            }
            catch (Exception ex)
            {
                upstream.LastError = ex.Message;
                _logger.LogWarning(ex, "Failed to list tools for upstream {Key}.", upstream.Key);
            }
        }

        _catalog = new AggregatedCatalog(tools, routes);
        CatalogChanged?.Invoke();
    }

    private static Tool NamespaceTool(Tool original, string exposedName, string serviceLabel) => new()
    {
        Name = exposedName,
        Title = original.Title,
        Description = string.IsNullOrEmpty(original.Description) ? $"[{serviceLabel}]" : $"[{serviceLabel}] {original.Description}",
        InputSchema = original.InputSchema,
    };

    private async Task DisposeClientAsync(UpstreamServer upstream)
    {
        if (upstream.Client is null)
            return;
        try { await upstream.Client.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error disposing upstream {Key} client.", upstream.Key); }
        upstream.Client = null;
    }
}
