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
    Task ConnectStdioAsync(string key, string displayName, string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string key, CancellationToken cancellationToken = default);
    Task DisconnectAllAsync();
}

/// <inheritdoc />
public sealed class UpstreamRegistry : IUpstreamRegistry
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<UpstreamRegistry> _logger;
    private readonly ConcurrentDictionary<string, UpstreamServer> _upstreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private volatile AggregatedCatalog _catalog = AggregatedCatalog.Empty;
    private Task? _reconnectLoop;

    public UpstreamRegistry(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<UpstreamRegistry>();
    }

    public IReadOnlyCollection<UpstreamServer> Upstreams => _upstreams.Values.ToArray();

    public AggregatedCatalog Catalog => _catalog;

    public event Action? CatalogChanged;

    public Task ConnectAsync(string key, string displayName, Uri endpoint, CancellationToken cancellationToken = default)
        => ConnectCoreAsync(key, displayName, endpoint.ToString(),
            () => new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.AutoDetect },
                _loggerFactory),
            cancellationToken);

    public Task ConnectStdioAsync(string key, string displayName, string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => ConnectCoreAsync(key, displayName, $"stdio: {command}",
            () => new StdioClientTransport(
                new StdioClientTransportOptions { Command = command, Arguments = arguments?.ToList() ?? [] },
                _loggerFactory),
            cancellationToken);

    private Task ConnectCoreAsync(string key, string displayName, string endpointLabel, Func<IClientTransport> transportFactory, CancellationToken cancellationToken)
    {
        var upstream = _upstreams.GetOrAdd(key, _ => new UpstreamServer { Key = key, DisplayName = displayName, Endpoint = endpointLabel });
        upstream.Endpoint = endpointLabel;
        upstream.TransportFactory = transportFactory;
        return AttemptAsync(upstream, cancellationToken);
    }

    private async Task AttemptAsync(UpstreamServer upstream, CancellationToken cancellationToken)
    {
        upstream.State = UpstreamState.Connecting;
        upstream.LastError = null;

        try
        {
            var client = await McpClient.CreateAsync(upstream.TransportFactory!(), clientOptions: null, _loggerFactory, cancellationToken);

            await DisposeClientAsync(upstream); // replace any prior client
            upstream.Client = client;
            upstream.State = UpstreamState.Connected;
            upstream.RetryDelay = TimeSpan.FromSeconds(10); // reset backoff on success
            _logger.LogInformation("Connected upstream {Key} ({Endpoint}).", upstream.Key, upstream.Endpoint);
            await RebuildAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            upstream.State = UpstreamState.Faulted;
            upstream.LastError = ex.Message;
            upstream.RetryDelay = TimeSpan.FromSeconds(Math.Min(60, Math.Max(10, upstream.RetryDelay.TotalSeconds * 2)));
            upstream.NextRetryAt = DateTimeOffset.Now + upstream.RetryDelay;
            _logger.LogWarning(ex, "Upstream {Key} ({Endpoint}) failed; retrying in {Delay}s.",
                upstream.Key, upstream.Endpoint, upstream.RetryDelay.TotalSeconds);
            await RebuildAsync(cancellationToken);
        }

        EnsureReconnectLoop();
    }

    private void EnsureReconnectLoop()
    {
        lock (_gate)
            _reconnectLoop ??= Task.Run(ReconnectLoopAsync);
    }

    private async Task ReconnectLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                List<UpstreamServer> due;
                lock (_gate)
                    due = _upstreams.Values
                        .Where(u => u.State == UpstreamState.Faulted && u.TransportFactory is not null && DateTimeOffset.Now >= u.NextRetryAt)
                        .ToList();

                foreach (var upstream in due)
                {
                    if (_shutdown.IsCancellationRequested)
                        break;
                    await AttemptAsync(upstream, _shutdown.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
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
        await _shutdown.CancelAsync();
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
