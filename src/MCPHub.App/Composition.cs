using MCPHub.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MCPHub.App;

/// <summary>
/// Composition root. Core domain services (release/download/process/config/proxy) are registered
/// here as the manager and proxy milestones land; for now it wires the view-models.
/// </summary>
public static class Composition
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // View-models
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ServicesViewModel>();
    }
}
