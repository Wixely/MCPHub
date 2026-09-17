using Avalonia.Controls;
using System.ComponentModel;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MCPHub.App.ViewModels;

namespace MCPHub.App.Views;

public partial class RouterView : UserControl
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private RouterViewModel? _observed;
    public RouterView()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => (DataContext as RouterViewModel)?.RefreshHostState();
        AttachedToVisualTree += (_, _) =>
        {
            _observed = DataContext as RouterViewModel;
            if (_observed is not null) _observed.PropertyChanged += OnEditorChanged;
            _observed?.RefreshHostState();
            _statusTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _statusTimer.Stop();
            if (_observed is not null) _observed.PropertyChanged -= OnEditorChanged;
            _observed = null;
        };
    }
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        var target = e.PropertyName switch
        {
            nameof(RouterViewModel.IsOutputEditorOpen) when _observed?.IsOutputEditorOpen == true => "OutputEditor",
            nameof(RouterViewModel.IsInputEditorOpen) when _observed?.IsInputEditorOpen == true => "InputEditor",
            nameof(RouterViewModel.GeneratedKey) when _observed?.HasGeneratedKey == true => "GeneratedKeyCard",
            _ => null,
        };
        if (target is not null)
            Dispatcher.UIThread.Post(() => this.FindControl<Border>(target)?.BringIntoView(), DispatcherPriority.Loaded);
    }
    private async void OnCopyUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RouterViewModel vm) await CopyAsync(vm.EndpointUrl);
    }
    private async void OnCopyKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RouterViewModel { HasGeneratedKey: true } vm) await CopyAsync(vm.GeneratedKey);
    }
    private async Task CopyAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch
        {
            if (DataContext is RouterViewModel vm) vm.StatusMessage = "Clipboard is unavailable. Try again.";
        }
    }
}
