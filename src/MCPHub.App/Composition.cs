using System;
using System.Net.Http.Headers;
using MCPHub.App.Proxy;
using MCPHub.App.ViewModels;
using MCPHub.AppHost;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Logging;
using MCPHub.Core.Process;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;
using MCPHub.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MCPHub.App;

/// <summary>
/// Composition root. Wires Core domain services and the view-models. Process/download/proxy services
/// are added here as later milestones land.
/// </summary>
public static class Composition
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();

        // Tee the proxy's own ILogger output into the log store so it surfaces on the Logs page.
        services.AddSingleton<ILoggerProvider, LogStoreLoggerProvider>();

        // Settings + secrets
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<ISecretStore, SecretStore>();
        services.AddTransient<GithubAuthHandler>();

        // Core infrastructure + service manager
        services.AddSingleton<IInstalledManifestStore, InstalledManifestStore>();
        services.AddSingleton<IReleaseService, ReleaseService>();
        services.AddSingleton<IConfigMergeService, ConfigMergeService>();
        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<IServiceManager, ServiceManager>();

        // Process supervision + log capture
        services.AddSingleton<ILogStore>(_ => new LogStore(capacity: 5000));
        services.AddSingleton<IServiceProcessHost, ServiceProcessHost>();

        // MCP proxy / aggregator
        services.AddSingleton<IUpstreamRegistry, UpstreamRegistry>();
        services.AddSingleton<ProxyHandlers>();
        services.AddSingleton<ProxyHost>();
        services.AddSingleton<ProxyCoordinator>();

        // HTTP clients: GitHub releases, a short-timeout health probe, and long-timeout downloads.
        services.AddHttpClient(ReleaseService.HttpClientName, ConfigureGithubClient)
            .AddHttpMessageHandler<GithubAuthHandler>();
        services.AddHttpClient(ServiceProcessHost.HealthClientName, client => client.Timeout = TimeSpan.FromSeconds(3));
        services.AddHttpClient(DownloadService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MCPHub/0.1");
        });

        // View-models
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ServicesViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<ProxyViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    private static void ConfigureGithubClient(HttpClient client)
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCPHub/0.1 (+https://github.com/Wixely)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        // Authorization (PAT) is added per-request by GithubAuthHandler.
    }
}
