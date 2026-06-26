using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using MCPHub.App.ViewModels;
using MCPHub.App.Views;
using MCPHub.Core.Process;
using Microsoft.Extensions.DependencyInjection;

namespace MCPHub.App;

public partial class App : Application
{
    /// <summary>Root DI container for the application (composition root).</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var collection = new ServiceCollection();
        Composition.ConfigureServices(collection);
        Services = collection.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // CommunityToolkit.Mvvm uses its own validation; remove Avalonia's duplicate plugin.
            DisableAvaloniaDataAnnotationValidation();

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };

            // Kill any running sub-server processes when MCPHub exits.
            desktop.ShutdownRequested += (_, _) =>
            {
                try { Services.GetService<IServiceProcessHost>()?.StopAllAsync().Wait(TimeSpan.FromSeconds(3)); }
                catch { /* best effort */ }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var toRemove = BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in toRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}
