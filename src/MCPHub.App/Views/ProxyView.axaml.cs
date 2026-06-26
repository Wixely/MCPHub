using Avalonia.Controls;
using Avalonia.Interactivity;
using MCPHub.App.ViewModels;

namespace MCPHub.App.Views;

public partial class ProxyView : UserControl
{
    public ProxyView() => InitializeComponent();

    private async void OnCopyUrl(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProxyViewModel vm)
            await CopyAsync(vm.EndpointUrl);
    }

    private async void OnCopySnippet(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProxyViewModel vm)
            await CopyAsync(vm.ClientSnippet);
    }

    private async System.Threading.Tasks.Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }
}
