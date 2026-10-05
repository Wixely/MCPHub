using MCPHub.Core.Infrastructure;
using MCPHub.Hub;
using MCPHub.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// MCPHub without a window: the same hub the desktop composes, hosted by a container's lifetime
// instead of a user's. Everything it serves — the MCP proxy, the Model Router, identity,
// permissions and the management tools — is HubComposition's; this file supplies only what a
// container decides for itself, which is which listeners come up and where.
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
