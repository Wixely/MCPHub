using MCPHub.Core.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A host for the model router only: no desktop composition root, AppPaths, service catalogue or UI.
try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
    var configPath = Environment.GetEnvironmentVariable("MCPHUB_ROUTER_CONFIG")
        ?? Path.Combine(AppContext.BaseDirectory, "router.example.json");
    var source = new RouterDeploymentSource(configPath);
    var options = new RouterHostOptions { BindAddress = Environment.GetEnvironmentVariable("MCPHUB_ROUTER_BIND") ?? "127.0.0.1" };
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

sealed class RouterLifetime(RouterHost router, RouterDeploymentSource source, ILogger<RouterLifetime> logger) : BackgroundService
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
                if (!source.Reload())
                {
                    if (!warned) logger.LogWarning("Router reload rejected; last valid routes remain active. Check configuration and secrets; listener changes require restart.");
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
