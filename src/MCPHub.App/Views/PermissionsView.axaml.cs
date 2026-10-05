using Avalonia.Controls;
using Avalonia.Input;
using MCPHub.App.ViewModels;

namespace MCPHub.App.Views;

public partial class PermissionsView : UserControl
{
    public PermissionsView()
    {
        InitializeComponent();

        // Whether any of this applies is decided on the Users page, so it is re-read on the way in
        // rather than when this page was last built.
        AttachedToVisualTree += (_, _) => (DataContext as PermissionsViewModel)?.RefreshPolicy();
    }

    /// <summary>
    /// Space ticks whatever is selected, which is how a long list of checkboxes is meant to be worked:
    /// select a run of rows with shift or control, press Space once.
    /// </summary>
    private void OnToolListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not ListBox list || DataContext is not PermissionsViewModel model)
        {
            return;
        }

        var chosen = list.SelectedItems?.OfType<ToolRow>().ToList() ?? [];
        if (chosen.Count == 0)
        {
            return;
        }

        model.ToggleTools(chosen);

        // Handled, or the ListBox also treats Space as "select just the focused row" and the selection
        // somebody built up disappears the moment they use it.
        e.Handled = true;
    }
}
