using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using MCPHub.App.Proxy;
using MCPHub.App.ViewModels;
using MCPHub.App.Views;
using MCPHub.Core.Process;
using MCPHub.Core.Settings;
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

        // Apply the persisted theme before showing any window.
        SettingsViewModel.ApplyTheme(Services.GetRequiredService<ISettingsStore>().Current.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // CommunityToolkit.Mvvm uses its own validation; remove Avalonia's duplicate plugin.
            DisableAvaloniaDataAnnotationValidation();

            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };

            // Start the aggregated MCP proxy endpoint and begin tracking running services.
            _ = Services.GetRequiredService<ProxyCoordinator>().StartAsync();

            // Stop the proxy and kill any running sub-server processes when MCPHub exits.
            desktop.ShutdownRequested += (_, _) =>
            {
                try { Services.GetService<ProxyCoordinator>()?.StopAsync().Wait(TimeSpan.FromSeconds(3)); }
                catch { /* best effort */ }
                try { Services.GetService<IServiceProcessHost>()?.StopAllAsync().Wait(TimeSpan.FromSeconds(3)); }
                catch { /* best effort */ }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OnTrayOpen(object? sender, EventArgs e) => ShowMainWindow();

    private void OnTrayExit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow window)
                window.ForceClose = true;
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var toRemove = BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in toRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}
