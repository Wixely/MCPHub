using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using MCPHub.App.Messages;
using MCPHub.Hub.Proxy;
using MCPHub.Core.Infrastructure;
using MCPHub.App.ViewModels;
using MCPHub.App.Views;
using MCPHub.Core.Process;
using MCPHub.Core.Settings;
using MCPHub.Core.Routing;
using MCPHub.Core.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace MCPHub.App;

public partial class App : Application
{
    /// <summary>Root DI container for the application (composition root).</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    /// <summary>The version this launch replaced, when it is the second half of a self-update; otherwise <see langword="null"/>.</summary>
    public static string? UpdatedFromVersion { get; set; }

    /// <summary>Why an update was rolled back, when this launch is the previous build put back; otherwise <see langword="null"/>.</summary>
    public static string? UpdateFailure { get; set; }

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

            var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = mainViewModel };

            if (UpdatedFromVersion is { } replaced)
                mainViewModel.ReportUpdated(replaced);
            if (UpdateFailure is { } failure)
                mainViewModel.ReportRestartFailure(failure + " Details are in update.log in the MCPHub data folder.");

            // The Updates page, once the new build is in place and waiting: shut down the way Exit does.
            WeakReferenceMessenger.Default.Register<RestartForUpdateMessage>(this, (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (desktop.MainWindow is MainWindow window)
                    window.ForceClose = true;
                desktop.Shutdown();
            }));

            // Looks for a newer MCPHub when the last answer is old enough, and says so in a banner.
            var checker = Services.GetRequiredService<HubUpdateChecker>();
            checker.UpdateAvailable += release => Dispatcher.UIThread.Post(() => mainViewModel.ReportUpdateAvailable(release.Version));
            checker.Start();

            // Start the aggregated MCP proxy endpoint and begin tracking running services.
            _ = Services.GetRequiredService<ProxyCoordinator>().StartAsync();
            _ = Services.GetRequiredService<RouterHost>().StartConfiguredAsync();

            // Stop the proxy and kill any running sub-server processes when MCPHub exits.
            desktop.ShutdownRequested += (_, _) =>
            {
                try { Services.GetService<RouterHost>()?.StopAsync().Wait(TimeSpan.FromSeconds(6)); }
                catch { /* best effort */ }
                // Writes any "last connected" times still buffered, so they survive to the next launch.
                try { Services.GetService<RouterActivityLog>()?.Flush(); }
                catch { /* best effort */ }
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

    /// <summary>
    /// Tray "Restart MCPHub": starts a replacement that waits for this process to release the single-instance
    /// lock, then shuts this one down exactly as Exit does — so every managed server is stopped cleanly and
    /// started again by the new process, rather than being orphaned.
    /// </summary>
    private void OnTrayRestart(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        // Nothing is shut down until a replacement is confirmed started; a failure here leaves MCPHub running.
        if (InstanceRestart.TryLaunchReplacement() is { } failure)
        {
            ShowMainWindow();
            if (desktop.MainWindow?.DataContext is MainWindowViewModel viewModel)
                viewModel.ReportRestartFailure(failure);
            return;
        }

        if (desktop.MainWindow is MainWindow window)
            window.ForceClose = true;
        desktop.Shutdown();
    }

    private void OnTrayExit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow window)
                window.ForceClose = true;
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// Brings the running instance's window up, on request from a second launch that the single-instance
    /// guard refused. Called from the activation listener's background thread, hence the dispatcher hop; the
    /// result is the same as clicking the tray icon, which is what starting MCPHub again is asking for.
    /// </summary>
    public static void RequestShowMainWindow() =>
        Dispatcher.UIThread.Post(() => (Current as App)?.ShowMainWindow());

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Show();
            // A window hidden to the tray while maximised should come back maximised, not shrink to normal.
            if (window.WindowState == WindowState.Minimized)
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
