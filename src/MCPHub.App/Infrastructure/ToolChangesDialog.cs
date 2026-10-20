using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MCPHub.App.ViewModels;
using MCPHub.App.Views;

namespace MCPHub.App.Infrastructure;

/// <summary>
/// Shows what refreshing one server changed about its tools.
///
/// <para>An interface for the same reason as <see cref="IConfirmation"/>: the Proxy page decides when to
/// show it, and is tested without a window.</para>
/// </summary>
public interface IToolChangesDialog
{
    /// <summary>Shows the summary and returns once it is closed. Does nothing with no window to show it over.</summary>
    Task ShowAsync(ToolChangesViewModel summary);
}

/// <summary>A modal window over the main one.</summary>
public sealed class ToolChangesDialog : IToolChangesDialog
{
    /// <inheritdoc />
    public async Task ShowAsync(ToolChangesViewModel summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
                return;

            await new ToolChangesWindow { DataContext = summary }.ShowDialog(owner);
        });
    }
}
