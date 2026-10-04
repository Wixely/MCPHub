using System;
using System.Net.Http.Headers;
using MCPHub.App.Proxy;
using MCPHub.App.ViewModels;
using MCPHub.Hosting;
using MCPHub.Core.Agent;
using MCPHub.Core.Slopworks;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Logging;
using MCPHub.Core.Backup;
using MCPHub.Core.Management;
using MCPHub.Core.Models;
using MCPHub.Core.Permissions;
using MCPHub.Core.Users;
using MCPHub.Core.Process;
using MCPHub.Core.Recipes;
using MCPHub.Core.Routing;
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
        // Run-at-sign-in lives in the OS (HKCU Run / XDG autostart), not settings.json, so the checkbox
        // always reflects what the machine will actually do.
        services.AddSingleton<IStartupRegistration>(_ => new StartupRegistration());
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<ISecretStore, SecretStore>();
        services.AddSingleton<RouterStore>();
        services.AddSingleton<IRouterConfigurationSource>(sp => sp.GetRequiredService<RouterStore>());
        // Per-agent "last connected", persisted beside router.json so it survives a restart of MCPHub.
        services.AddSingleton<RouterActivityLog>();
        services.AddSingleton<IRouterActivityLog>(sp => sp.GetRequiredService<RouterActivityLog>());
        // No RouterHostOptions is registered: the desktop takes its bind address from router.json, so the
        // Router page can change it and rebind without restarting MCPHub.
        services.AddSingleton<RouterHost>();
        services.AddSingleton<IRouterOutputTester>(_ => new RouterOutputTester());
        services.AddTransient<GithubAuthHandler>();

        // Settings archive: selective export / import of configuration as a zip, optionally encrypted.
        services.AddSingleton<ISettingsArchiveService, SettingsArchiveService>();

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

        // Who may use which tools, and the tools for managing that. The hub is authoritative: a caller
        // presents a key, is resolved to one user, and sees only what that user is granted.
        // Off in effect until keys are enforced (AllowUnauthenticated false), which is what keeps the
        // single-user desktop behaving exactly as it always has.
        // Identity, shared by every surface: one user, one key, usable on the proxy and on the Router.
        services.AddSingleton<UserStore>();
        services.AddSingleton<IUserDirectory>(sp => sp.GetRequiredService<UserStore>());
        services.AddSingleton<IWritableUsers>(sp => sp.GetRequiredService<UserStore>());

        // What each user may use. Grants only — the directory above says who they are.
        services.AddSingleton(sp => new PermissionsStore(
            sp.GetRequiredService<IAppPaths>(), sp.GetRequiredService<IWritableUsers>()));
        services.AddSingleton<IPermissionsConfigurationSource>(sp => sp.GetRequiredService<PermissionsStore>());
        services.AddSingleton<IWritablePermissions>(sp => sp.GetRequiredService<PermissionsStore>());

        // Everything keyed to a user, so deleting one leaves no grant and no route behind naming an id
        // nobody holds. Each layer drops its own entry; identity never reaches into either.
        services.AddSingleton<IUserDependent>(sp => sp.GetRequiredService<PermissionsStore>());
        services.AddSingleton<IUserDependent>(sp => sp.GetRequiredService<RouterStore>());
        services.AddSingleton<PermissionsToolAuthorization>();

        // One switch for both administration surfaces, off by default: they govern every other tool, so
        // without it the widest grant would silently amount to administrator.
        services.AddSingleton<AdministrationPolicy>();

        // MCP proxy / aggregator
        services.AddSingleton<IUpstreamRegistry, UpstreamRegistry>();

        // What tools exist, for the Permissions page's picker. A snapshot each time it is asked, since
        // services connect and disconnect and a cached list would offer tools that are no longer there.
        services.AddSingleton<IToolCatalog>(sp => new ToolCatalog(
            sp.GetRequiredService<IUpstreamRegistry>(), sp.GetServices<ILocalToolProvider>()));
        // Explicit factory: registering ProxyHandlers by type makes the container fall back to the
        // registry-only constructor (the policy overload has a non-defaulted parameter it cannot
        // resolve), which would silently drop the in-process tool providers.
        // Registered rather than built inline, because the permissions tools need the same stack they
        // are governed by: permissions__explain answers "why can this caller not use this tool" across
        // every policy, and a second composite built for it could drift from the one that enforces.
        services.AddSingleton(sp => new CompositeToolAuthorization(
            sp.GetRequiredService<RecipeAccessPolicy>(),
            sp.GetRequiredService<AgentManagementPolicy>(),
            sp.GetRequiredService<AdministrationPolicy>(),
            sp.GetRequiredService<PermissionsToolAuthorization>()));
        services.AddSingleton<IToolAuthorization>(sp => sp.GetRequiredService<CompositeToolAuthorization>());
        services.AddSingleton<ILocalToolProvider>(sp => new UserToolProvider(
            sp.GetRequiredService<IUserDirectory>(),
            sp.GetServices<IUserDependent>(),
            sp.GetRequiredService<ILogger<UserToolProvider>>()));
        services.AddSingleton<ILocalToolProvider>(sp => new PermissionsToolProvider(
            sp.GetRequiredService<IUserDirectory>(),
            sp.GetRequiredService<IPermissionsConfigurationSource>(),
            sp.GetRequiredService<CompositeToolAuthorization>(),
            sp.GetRequiredService<ILogger<PermissionsToolProvider>>()));
        services.AddSingleton(sp => new ProxyHandlers(
            sp.GetRequiredService<IUpstreamRegistry>(),
            authorization: sp.GetRequiredService<CompositeToolAuthorization>(),
            auditSink: null,
            tenantResolver: ClaimsTenantResolver.Instance,
            localToolProviders: sp.GetServices<ILocalToolProvider>()));
        // Instructions are captured when the host is built, so they reflect the policies at launch (and after a
        // proxy restart); tool visibility itself follows the checkboxes live.
        services.AddSingleton(sp =>
        {
            var permissions = sp.GetRequiredService<IPermissionsConfigurationSource>();
            var users = sp.GetRequiredService<IUserDirectory>();
            return new ProxyHost(
                sp.GetRequiredService<ProxyHandlers>(),
                sp.GetRequiredService<ILoggerFactory>(),
                new ProxyHostOptions
                {
                    ServerInstructions = CombineInstructions(
                        sp.GetRequiredService<IRecipeAccessPolicy>().ServerInstructions,
                        sp.GetRequiredService<IAgentManagementPolicy>().ServerInstructions),

                    // Always wired, so a key issued later works without restarting the proxy; whether a
                    // caller may present NO key is asked per request, so turning enforcement on and off
                    // takes effect live too.
                    TenantAuthenticator = new DelegatingTenantAuthenticator(key =>
                        users.Resolve(key) is { } user ? new TenantContext(user.Id) : null),
                    AllowAnonymous = () => permissions.Snapshot.AllowUnauthenticated,
                });
        });
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
        services.AddSingleton<RouterViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<RecipesViewModel>();
        services.AddSingleton<UsersViewModel>();
        services.AddSingleton<PermissionsViewModel>();
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
