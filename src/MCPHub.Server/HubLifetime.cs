using MCPHub.Core.Routing;
using MCPHub.Core.Settings;
using MCPHub.Hub.Proxy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPHub.Server;

/// <summary>
/// Starting and stopping the hub's listeners with the process.
///
/// <para>The desktop does this from its window's lifetime and two checkboxes. A container has
/// neither: it is running because somebody started it, so whatever was asked for comes up whatever
/// the stored "start on launch" settings say — a hub that started and served nothing would be
/// indistinguishable from a broken one.</para>
///
/// <para>And if something that <em>was</em> asked for cannot start, the process does not: an
/// operator who asked for the Router and silently got a hub without it is the same failure one step
/// later, with no log line to look at.</para>
/// </summary>
internal sealed class HubLifetime(
    ProxyCoordinator coordinator,
    RouterStore routes,
    RouterHost router,
    ISettingsStore settings,
    ServerOptions options,
    ILogger<HubLifetime> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Administration is { } administration)
        {
            settings.Current.AdministrationEnabled = administration;
        }

        if (options.Proxy)
        {
            // The listener is the deployment's, not the stored setting's: a port chosen on somebody's
            // desktop and carried in on a mounted config would leave the container's published port
            // pointing at nothing.
            settings.Current.ProxyBindAddress = options.BindAddress;
            settings.Current.ProxyPort = options.Port;
            settings.Current.StartProxyOnLaunch = true;
        }

        await settings.SaveAsync(cancellationToken).ConfigureAwait(false);

        if (options.Proxy)
        {
            await coordinator.StartAsync().ConfigureAwait(false);
            logger.LogInformation(
                "Proxy listening on {Endpoint}. Administration tools are {State}.",
                coordinator.Host.EndpointUrl,
                settings.Current.AdministrationEnabled ? "on" : "off");
        }

        if (options.Router)
        {
            await StartRouterAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (options.Router)
        {
            await router.StopAsync().ConfigureAwait(false);
        }

        if (options.Proxy)
        {
            await coordinator.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task StartRouterAsync()
    {
        if (routes.LoadError is { } error)
        {
            // Asked for and unable to start. Named rather than generic: the fix is in a file this
            // message can point at, and no part of that file is quoted into a container's logs.
            throw new InvalidOperationException(
                $"The Model Router was asked for and cannot start. {error}");
        }

        routes.Configure(options.RouterBindAddress, options.RouterPort, startOnLaunch: true);
        await router.StartAsync().ConfigureAwait(false);

        var configured = routes.Snapshot;
        logger.LogInformation(
            "Model Router listening on {Endpoint} with {Outputs} output(s) and {Routes} user(s) routed.",
            router.EndpointUrl,
            configured.Outputs.Length,
            configured.Inputs.Length);

        if (configured.Outputs.Length == 0)
        {
            // Every request will answer 503 until an output exists. Worth a line: the listener is up,
            // so nothing else about the container looks wrong.
            logger.LogWarning(
                "The Model Router has no outputs, so every request will be refused until one is added.");
        }
    }
}
