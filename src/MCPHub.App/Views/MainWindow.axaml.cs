using System;
using Avalonia;
using Avalonia.Controls;
using MCPHub.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace MCPHub.App.Views;

public partial class MainWindow : Window
{
    /// <summary>Set true by the tray "Exit" command so close-to-tray doesn't cancel a real shutdown.</summary>
    public bool ForceClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        RestoreSize();
        PropertyChanged += OnWindowPropertyChanged;
        Closing += OnWindowClosing;
    }

    private ISettingsStore? SettingsStore => App.Services?.GetService<ISettingsStore>();

    private MCPHubSettings? Settings => SettingsStore?.Current;

    /// <summary>Restores the remembered window size (not position) from settings.</summary>
    private void RestoreSize()
    {
        if (Settings is not { } settings)
            return;

        if (settings.WindowWidth >= MinWidth && settings.WindowWidth <= 10_000)
            Width = settings.WindowWidth;
        if (settings.WindowHeight >= MinHeight && settings.WindowHeight <= 10_000)
            Height = settings.WindowHeight;
    }

    /// <summary>Persists the current window size (only while in the normal, non-maximized state).</summary>
    private void SaveSize()
    {
        if (SettingsStore is not { } store || WindowState != WindowState.Normal)
            return;

        store.Current.WindowWidth = ClientSize.Width;
        store.Current.WindowHeight = ClientSize.Height;
        _ = store.SaveAsync();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty
            && e.NewValue is WindowState.Minimized
            && OperatingSystem.IsWindows()
            && Settings is { MinimizeToTray: true })
        {
            Hide();
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveSize();

        if (!ForceClose && OperatingSystem.IsWindows() && Settings is { CloseToTray: true })
        {
            e.Cancel = true;
            Hide();
        }
    }
}
