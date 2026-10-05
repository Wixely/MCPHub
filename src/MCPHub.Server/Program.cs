using MCPHub.Core.Infrastructure;
using MCPHub.Core.Settings;
using MCPHub.Hub;
using MCPHub.Hub.Proxy;
using MCPHub.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// MCPHub without a window: the same hub the desktop composes, hosted by a container's lifetime
// instead of a user's. Everything it serves — the proxy, identity, permissions, the management
// tools — is HubComposition's; this file supplies only what a container decides for itself.
try
{
    var options = ServerOptions.FromEnvironment();

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Services.AddSingleton<IAppPaths>(new ContainerPaths());
    HubComposition.ConfigureHub(builder.Services);
    builder.Services.AddSingleton(options);
    builder.Services.AddHostedService<HubLifetime>();
    builder.Services.AddWindowsService(o => o.ServiceName = "MCPHub");
    builder.Services.AddSystemd();

    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    // Nothing of the configuration is quoted: a users document and a permissions document are what
    // this process reads, and neither belongs in a container's logs by accident.
    Console.Error.WriteLine("MCPHub could not start: " + ex.Message);
    return 1;
}

/// <summary>
/// Starting and stopping the hub with the process.
///
/// <para>The desktop does this from its window's lifetime and a checkbox. A container has neither:
/// it is running because somebody started it, so the proxy comes up whatever
/// <c>StartProxyOnLaunch</c> says — a hub that started and served nothing would be indistinguishable
/// from a broken one.</para>
/// </summary>
internal sealed class HubLifetime(
    ProxyCoordinator coordinator,
    ISettingsStore settings,
    ServerOptions options,
    ILogger<HubLifetime> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The listener is the deployment's, not the stored setting's: a port chosen on somebody's
        // desktop and carried in on a mounted config would leave the container's published port
        // pointing at nothing.
        settings.Current.ProxyBindAddress = options.BindAddress;
        settings.Current.ProxyPort = options.Port;
        settings.Current.StartProxyOnLaunch = true;

        if (options.Administration is { } administration)
        {
            settings.Current.AdministrationEnabled = administration;
        }

        await settings.SaveAsync(cancellationToken).ConfigureAwait(false);
        await coordinator.StartAsync().ConfigureAwait(false);

        logger.LogInformation(
            "MCPHub is listening on {Endpoint}. Administration tools are {State}.",
            coordinator.Host.EndpointUrl,
            settings.Current.AdministrationEnabled ? "on" : "off");
    }

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await coordinator.StopAsync().ConfigureAwait(false);
}
