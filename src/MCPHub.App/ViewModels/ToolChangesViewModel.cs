using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using MCPHub.Proxy;

namespace MCPHub.App.ViewModels;

/// <summary>
/// The summary shown after refreshing one server: which of its tools were added, renamed, changed or
/// removed since it was last listed, or why the refresh produced nothing to compare.
/// </summary>
public sealed class ToolChangesViewModel
{
    private ToolChangesViewModel(string title, string summary, string? note, IReadOnlyList<ToolChangeRow> rows)
    {
        Title = title;
        Summary = summary;
        Note = note;
        Rows = rows;
    }

    public string Title { get; }

    /// <summary>One line: what changed, in counts, or that nothing did.</summary>
    public string Summary { get; }

    /// <summary>A caveat worth reading before the rows, or <see langword="null"/>.</summary>
    public string? Note { get; }

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public IReadOnlyList<ToolChangeRow> Rows { get; }

    public bool HasRows => Rows.Count > 0;

    /// <summary>The server reconnected and was listed again: compare the two listings.</summary>
    /// <param name="wasConnected">
    /// Whether the earlier listing came from a live connection. A server that was faulted has no earlier
    /// listing, so every tool reads as new — which is true of what the proxy offered, but worth saying.
    /// </param>
    public static ToolChangesViewModel ForChanges(string serverName, ToolCatalogChanges changes, bool wasConnected)
    {
        var title = $"Tools on {serverName}";

        string summary;
        if (!changes.HasChanges)
        {
            summary = "No changes.";
        }
        else
        {
            var parts = new List<string>();
            void Add(ToolChangeKind kind, string word)
            {
                var n = changes.Count(kind);
                if (n > 0)
                    parts.Add($"{n} {word}");
            }

            Add(ToolChangeKind.Added, "added");
            Add(ToolChangeKind.Renamed, "renamed");
            Add(ToolChangeKind.Changed, "changed");
            Add(ToolChangeKind.Removed, "removed");
            summary = $"{string.Join(", ", parts)}; {changes.UnchangedCount} unchanged. " +
                      $"{changes.ToolCountBefore} tool(s) before, {changes.ToolCountAfter} now.";
        }

        var note = wasConnected
            ? (changes.Count(ToolChangeKind.Renamed) > 0
                ? "Renames are inferred from matching parameters or descriptions. The server does not report them."
                : null)
            : "The server was not connected before this refresh, so there was no earlier list to compare against and every tool shows as added.";

        return new ToolChangesViewModel(title, summary, note, changes.Changes.Select(ToolChangeRow.From).ToList());
    }

    /// <summary>The refresh did not get a listing: say so, since the tools the proxy offers are unchanged only in the sense that it now offers none.</summary>
    public static ToolChangesViewModel ForFailure(string serverName, string? error)
        => new(
            $"Tools on {serverName}",
            "The server could not be reconnected, so its tools could not be listed again.",
            string.IsNullOrWhiteSpace(error) ? null : error,
            []);
}

/// <summary>One changed tool, as a row in the summary.</summary>
public sealed class ToolChangeRow
{
    private static readonly IBrush AddedBrush = Brush.Parse("#2EA043");
    private static readonly IBrush RenamedBrush = Brush.Parse("#8957E5");
    private static readonly IBrush ChangedBrush = Brush.Parse("#D29922");
    private static readonly IBrush RemovedBrush = Brush.Parse("#DA3633");

    private ToolChangeRow(string kindText, IBrush kindBrush, string name, IReadOnlyList<string> details)
    {
        KindText = kindText;
        KindBrush = kindBrush;
        Name = name;
        Details = details;
    }

    public string KindText { get; }
    public IBrush KindBrush { get; }
    public string Name { get; }
    public IReadOnlyList<string> Details { get; }

    public static ToolChangeRow From(ToolChange change) => change.Kind switch
    {
        ToolChangeKind.Added => new("Added", AddedBrush, change.Name, change.Details),
        ToolChangeKind.Renamed => new("Renamed", RenamedBrush, change.Name, change.Details),
        ToolChangeKind.Changed => new("Changed", ChangedBrush, change.Name, change.Details),
        _ => new("Removed", RemovedBrush, change.Name, change.Details),
    };
}
