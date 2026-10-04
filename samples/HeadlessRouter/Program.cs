using MCPHub.Core.Routing;
using MCPHub.Core.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A host for the model router only: no desktop composition root, AppPaths, service catalogue or UI.
try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
    var configPath = Environment.GetEnvironmentVariable("MCPHUB_ROUTER_CONFIG")
        ?? Path.Combine(AppContext.BaseDirectory, "router.example.json");
    // Identity is its own document now, as it is on the desktop: the Router says which user goes where,
    // the users file says who holds which key. One key then works here and on the proxy, and suspending a
    // user in that file stops both.
    var usersPath = Environment.GetEnvironmentVariable("MCPHUB_USERS_CONFIG")
        ?? Path.Combine(AppContext.BaseDirectory, "users.example.json");
    var users = new UsersDeploymentSource(usersPath);
    var source = new RouterDeploymentSource(configPath, users);
    // Null leaves the bind address to the configuration file, which applies MCPHUB_ROUTER_BIND itself.
    var options = new RouterHostOptions();
    builder.Services.AddSingleton(users);
    builder.Services.AddSingleton<IUserDirectory>(users);
    builder.Services.AddSingleton(source);
    builder.Services.AddSingleton<IRouterConfigurationSource>(source);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<RouterHost>();
    builder.Services.AddHostedService<RouterLifetime>();
    builder.Services.AddWindowsService(o => o.ServiceName = "MCPHub Model Router");
    builder.Services.AddSystemd();
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Headless router could not run. Check configuration, secret sources, bind address and port.");
    return 1;
}

sealed class RouterLifetime(
    RouterHost router, RouterDeploymentSource source, UsersDeploymentSource users, ILogger<RouterLifetime> logger)
    : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await router.StartAsync(cancellationToken: cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var warned = false;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Both documents, every tick: a key added to one and a route to the other should take
                // effect together, and reloading only half would apply a route for a user not yet there.
                if (!users.Reload() || !source.Reload())
                {
                    if (!warned) logger.LogWarning("Router or users reload rejected; the last valid set remains active. Check both documents and their secrets; listener changes require restart.");
                    warned = true;
                }
                else if (warned)
                {
                    logger.LogInformation("Router configuration reload recovered.");
                    warned = false;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await router.StopAsync();
    }
}
