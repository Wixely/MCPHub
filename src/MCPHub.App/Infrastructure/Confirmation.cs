using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MCPHub.App.Views;

namespace MCPHub.App.Infrastructure;

/// <summary>What to ask, and what the two answers are called.</summary>
/// <param name="Title">The question, in a few words.</param>
/// <param name="Message">What happens if they say yes — the part worth reading.</param>
/// <param name="ConfirmText">The button that goes ahead, named after the action rather than "OK".</param>
/// <param name="CancelText">The button that does not.</param>
public sealed record ConfirmRequest(string Title, string Message, string ConfirmText, string CancelText);

/// <summary>
/// Asking before something takes effect.
///
/// <para>An interface because the choices worth confirming are decided in view models, which are
/// tested without a window: a test answers yes or no, and the page under test is none the wiser.</para>
/// </summary>
public interface IConfirmation
{
    /// <summary>True when the person said yes. False for no, for a closed window, and for a headless
    /// process with nobody to ask — the caller should treat that as "do not proceed".</summary>
    Task<bool> AskAsync(ConfirmRequest request);
}

/// <summary>A modal window over the main one, so the choice cannot be left half-made by navigating away.</summary>
public sealed class DialogConfirmation : IConfirmation
{
    /// <inheritdoc />
    public async Task<bool> AskAsync(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            {
                // No window to be modal over: nobody is there to agree, so nothing goes ahead.
                return false;
            }

            return await new ConfirmWindow { DataContext = request }.ShowDialog<bool>(owner);
        });
    }
}
