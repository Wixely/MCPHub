using System;
using System.Net.Http.Headers;
using MCPHub.App.ViewModels;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Logging;
using MCPHub.Core.Process;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using Microsoft.Extensions.DependencyInjection;

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

        // Core infrastructure + service manager
        services.AddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<IInstalledManifestStore, InstalledManifestStore>();
        services.AddSingleton<IReleaseService, ReleaseService>();
        services.AddSingleton<IServiceManager, ServiceManager>();

        // Process supervision + log capture
        services.AddSingleton<ILogStore>(_ => new LogStore(capacity: 5000));
        services.AddSingleton<IServiceProcessHost, ServiceProcessHost>();

        // HTTP clients: GitHub releases, and a short-timeout health probe.
        services.AddHttpClient(ReleaseService.HttpClientName, ConfigureGithubClient);
        services.AddHttpClient(ServiceProcessHost.HealthClientName, client => client.Timeout = TimeSpan.FromSeconds(3));

        // View-models
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ServicesViewModel>();
        services.AddSingleton<LogsViewModel>();
    }

    private static void ConfigureGithubClient(HttpClient client)
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCPHub/0.1 (+https://github.com/Wixely)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var pat = Environment.GetEnvironmentVariable("MCPHUB_GITHUB_PAT");
        if (!string.IsNullOrWhiteSpace(pat))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
    }
}
