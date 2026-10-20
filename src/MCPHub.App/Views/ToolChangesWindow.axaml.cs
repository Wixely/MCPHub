using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MCPHub.App.Views;

/// <summary>What refreshing one server changed about its tools. Read-only: the one button closes it.</summary>
public partial class ToolChangesWindow : Window
{
    public ToolChangesWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
