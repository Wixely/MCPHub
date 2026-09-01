using System;
using System.Net.Http.Headers;
using MCPHub.App.Proxy;
using MCPHub.App.ViewModels;
using MCPHub.Hosting;
using MCPHub.Core.Agent;
using MCPHub.Core.Slopworks;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Logging;
using MCPHub.Core.Management;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Recipes;
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

        // Recipes knowledge base: persisted beside settings.json, edited on the Recipes page and by agents
        // through the proxy's recipes__* tools (registered as an in-process tool provider below).
        services.AddSingleton<IRecipeStore, RecipeStore>();
        services.AddSingleton<ILocalToolProvider, RecipeToolProvider>();
        // What agents may do with recipes (off / read-only / read-write): two settings, each overridable by a
        // MCPHUB_RECIPES_* environment variable for headless deployments. Enforced as the proxy's tool authorization.
        services.AddSingleton<RecipeAccessPolicy>();
        services.AddSingleton<IRecipeAccessPolicy>(sp => sp.GetRequiredService<RecipeAccessPolicy>());

        // Agent management: lets agents list / start / stop / restart / install / update the managed servers and
        // check for updates (servers and MCPHub) through the proxy's mcphub__* tools. Off by default; a master
        // switch plus three capability switches in Settings, each overridable by a MCPHUB_AGENT_MANAGEMENT_*
        // environment variable. Config files and logs are never exposed.
        services.AddSingleton<ILocalToolProvider, AgentManagementToolProvider>();
        services.AddSingleton<AgentManagementPolicy>();
        services.AddSingleton<IAgentManagementPolicy>(sp => sp.GetRequiredService<AgentManagementPolicy>());

        // MCP proxy / aggregator
        services.AddSingleton<IUpstreamRegistry, UpstreamRegistry>();
        // Explicit factory: registering ProxyHandlers by type makes the container fall back to the
        // registry-only constructor (the policy overload has a non-defaulted parameter it cannot
        // resolve), which would silently drop the in-process tool providers.
        services.AddSingleton(sp => new ProxyHandlers(
            sp.GetRequiredService<IUpstreamRegistry>(),
            authorization: new CompositeToolAuthorization(
                sp.GetRequiredService<RecipeAccessPolicy>(),
                sp.GetRequiredService<AgentManagementPolicy>()),
            auditSink: null,
            tenantResolver: null,
            localToolProviders: sp.GetServices<ILocalToolProvider>()));
        // Instructions are captured when the host is built, so they reflect the policies at launch (and after a
        // proxy restart); tool visibility itself follows the checkboxes live.
        services.AddSingleton(sp => new ProxyHost(
            sp.GetRequiredService<ProxyHandlers>(),
            sp.GetRequiredService<ILoggerFactory>(),
            new ProxyHostOptions
            {
                ServerInstructions = CombineInstructions(
                    sp.GetRequiredService<IRecipeAccessPolicy>().ServerInstructions,
                    sp.GetRequiredService<IAgentManagementPolicy>().ServerInstructions),
            }));
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

        // DaggerAgent — a managed agent app installed into its own folder with selectable run modes.
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            var settings = sp.GetRequiredService<ISettingsStore>();
            var folder = string.IsNullOrWhiteSpace(settings.Current.AgentFolder)
                ? Path.Combine(paths.DataDirectory, "agent")
                : settings.Current.AgentFolder!;
            return new AgentContext(new ManagedService(DaggerAgent.Catalog, folder));
        });
        services.AddSingleton<IAgentProcessHost, AgentProcessHost>();
        services.AddSingleton<IAgentService, AgentService>();

        // Slopworks — vLLM setup/management tool (not an MCP server). MCPHub installs the binary
        // from GitHub releases and shells out to its CLI for start / stop / status.
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            var settings = sp.GetRequiredService<ISettingsStore>();
            var folder = string.IsNullOrWhiteSpace(settings.Current.SlopworksFolder)
                ? Path.Combine(paths.DataDirectory, "slopworks")
                : settings.Current.SlopworksFolder!;
            return new SlopworksContext(new ManagedService(Slopworks.Catalog, folder));
        });
        services.AddSingleton<ISlopworksService, SlopworksService>();
        services.AddSingleton<ISlopworksCli, SlopworksCli>();
        services.AddSingleton<ISlopworksDaggerBridge, SlopworksDaggerBridge>();

        // View-models
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ServicesViewModel>();
        services.AddSingleton<AgentViewModel>();
        services.AddSingleton<SlopworksViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<ProxyViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<RecipesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<UpdatesViewModel>();
    }

    /// <summary>Joins the per-feature MCP server instructions; <see langword="null"/> when every feature is off.</summary>
    private static string? CombineInstructions(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join("\n\n", present);
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
