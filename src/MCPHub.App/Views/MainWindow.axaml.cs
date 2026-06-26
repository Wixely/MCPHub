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
        PropertyChanged += OnWindowPropertyChanged;
        Closing += OnWindowClosing;
    }

    private MCPHubSettings? Settings => App.Services?.GetService<ISettingsStore>()?.Current;

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
        if (!ForceClose && OperatingSystem.IsWindows() && Settings is { CloseToTray: true })
        {
            e.Cancel = true;
            Hide();
        }
    }
}
