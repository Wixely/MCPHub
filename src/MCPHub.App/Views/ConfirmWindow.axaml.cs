using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MCPHub.App.Views;

/// <summary>
/// A yes/no question over the main window. Closing it any other way — the title bar, Escape — answers
/// no, since a question nobody answered is not consent.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
