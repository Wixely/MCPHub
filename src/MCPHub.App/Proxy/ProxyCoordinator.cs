using System;
using System.Threading.Tasks;
using MCPHub.AppHost;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
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
    private readonly ILogger<ProxyCoordinator> _logger;

    public ProxyCoordinator(
        IServiceProcessHost processHost,
        IUpstreamRegistry registry,
        ProxyHost proxyHost,
        ILogger<ProxyCoordinator> logger)
    {
        _processHost = processHost;
        Registry = registry;
        _proxyHost = proxyHost;
        _logger = logger;
    }

    public ProxyHost Host => _proxyHost;

    public IUpstreamRegistry Registry { get; }

    public async Task StartAsync()
    {
        _processHost.StateChanged += OnServiceStateChanged;
        try
        {
            await _proxyHost.StartAsync(_proxyHost.BindAddress, _proxyHost.Port);
            _logger.LogInformation("Proxy listening at {Endpoint}.", _proxyHost.EndpointUrl);
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
