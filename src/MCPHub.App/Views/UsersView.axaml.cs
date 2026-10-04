using Avalonia.Controls;
using Avalonia.Interactivity;
using MCPHub.App.ViewModels;

namespace MCPHub.App.Views;

public partial class UsersView : UserControl
{
    public UsersView() => InitializeComponent();

    private async void OnCopyKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not UsersViewModel { HasGeneratedKey: true } model)
        {
            return;
        }

        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(model.GeneratedKey);
            }
        }
        catch
        {
            // The key is on screen either way, so a clipboard that refuses is an inconvenience rather
            // than a loss — but saying nothing would leave somebody pasting what they did not copy.
            model.StatusMessage = "Clipboard is unavailable. Copy the key from the box above.";
        }
    }
}
