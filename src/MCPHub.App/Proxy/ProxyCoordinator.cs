using System;
using System.Linq;
using System.Threading.Tasks;
using MCPHub.AppHost;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Settings;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging;

namespace MCPHub.App.Proxy;

/// <summary>
/// Bridges service process lifecycle to the proxy: connects an upstream when a managed service becomes
/// healthy (Running) and disconnects it when it stops or faults. Also owns starting/stopping the proxy host.
/// </summary>
public sealed class ProxyCoordinator
{
    private readonly IServiceProcessHost _processHost;
    private readonly ProxyHost _proxyHost;
    private readonly ISettingsStore _settings;
    private readonly ILogger<ProxyCoordinator> _logger;

    public ProxyCoordinator(
        IServiceProcessHost processHost,
        IUpstreamRegistry registry,
        ProxyHost proxyHost,
        ISettingsStore settings,
        ILogger<ProxyCoordinator> logger)
    {
        _processHost = processHost;
        Registry = registry;
        _proxyHost = proxyHost;
        _settings = settings;
        _logger = logger;
    }

    public ProxyHost Host => _proxyHost;

    public IUpstreamRegistry Registry { get; }

    public async Task StartAsync()
    {
        _processHost.StateChanged += OnServiceStateChanged;

        var settings = _settings.Current;
        _proxyHost.Configure(settings.ProxyBindAddress, settings.ProxyPort);

        await RefreshUserServersAsync();

        if (!settings.StartProxyOnLaunch)
            return;

        try
        {
            await _proxyHost.StartAsync(settings.ProxyBindAddress, settings.ProxyPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start the proxy host.");
        }
    }

    public async Task StopAsync()
    {
        _processHost.StateChanged -= OnServiceStateChanged;
        await Registry.DisconnectAllAsync();
        await _proxyHost.StopAsync();
    }

    /// <summary>Reconnects all enabled user-added servers (drops the previous set first).</summary>
    public async Task RefreshUserServersAsync()
    {
        foreach (var upstream in Registry.Upstreams.Where(u => u.Key.StartsWith("user-", StringComparison.Ordinal)).ToList())
            await Registry.DisconnectAsync(upstream.Key);

        foreach (var definition in _settings.Current.UserServers.Where(d => d.Enabled))
            await ConnectUserServerAsync(definition);
    }

    private async Task ConnectUserServerAsync(UserMcpServerDefinition definition)
    {
        var name = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Key : definition.DisplayName;
        try
        {
            if (definition.Kind == McpTransportKind.Http && Uri.TryCreate(definition.Endpoint, UriKind.Absolute, out var uri))
                await Registry.ConnectAsync(definition.Key, name, uri);
            else if (definition.Kind == McpTransportKind.Stdio && !string.IsNullOrWhiteSpace(definition.Command))
                await Registry.ConnectStdioAsync(definition.Key, name, definition.Command!, definition.Arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect user server {Name}.", name);
        }
    }

    private void OnServiceStateChanged(ManagedService service) => _ = SyncUpstreamAsync(service);

    private async Task SyncUpstreamAsync(ManagedService service)
    {
        try
        {
            if (service.RunState == ServiceRunState.Running && service.EndpointUrl is { } url)
                await Registry.ConnectAsync(service.Catalog.Key, service.Catalog.DisplayName, new Uri(url));
            else if (service.RunState is ServiceRunState.Stopped or ServiceRunState.Faulted)
                await Registry.DisconnectAsync(service.Catalog.Key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxy upstream sync failed for {Service}.", service.Catalog.Name);
        }
    }
}
