using MCPHub.App.Infrastructure;
using MCPHub.App.ViewModels;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Settings;
using MCPHub.Hub;
using Microsoft.Extensions.DependencyInjection;

namespace MCPHub.App;

/// <summary>
/// The desktop's composition root: the hub, plus the parts of it that only make sense with a window.
///
/// <para>Everything that is MCPHub rather than MCPHub-on-a-desktop lives in
/// <see cref="HubComposition"/>, which the headless server uses too. What is added here is a user
/// profile to keep files in, the run-at-sign-in registration, a way to ask the person a question,
/// and the view-models.</para>
/// </summary>
public static class Composition
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Where a desktop keeps its files. The hub does not assume this: a container mounts a volume
        // instead, which is the one thing the two heads genuinely disagree about.
        services.AddSingleton<IAppPaths, AppPaths>();

        // Run-at-sign-in lives in the OS (HKCU Run / XDG autostart), not settings.json, so the
        // checkbox always reflects what the machine will actually do.
        services.AddSingleton<IStartupRegistration>(_ => new StartupRegistration());

        HubComposition.ConfigureHub(services);

        // View-models
        // Asking before something takes effect. A window, so a choice cannot be left half-made by
        // navigating away from the page that asked.
        services.AddSingleton<IConfirmation, DialogConfirmation>();

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
}
