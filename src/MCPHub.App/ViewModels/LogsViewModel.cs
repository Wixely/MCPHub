using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Catalog;
using MCPHub.Core.Logging;

namespace MCPHub.App.ViewModels;

/// <summary>Shows a selected service's captured stdout/stderr, live-tailing new lines.</summary>
public sealed partial class LogsViewModel : ViewModelBase
{
    private readonly ILogStore _logStore;

    [ObservableProperty]
    private string? _selectedService;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _filterText = string.Empty;

    public ObservableCollection<string> ServiceNames { get; }

    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>Raised after lines change so the view can tail-scroll.</summary>
    public event Action? LinesChanged;

    public LogsViewModel(ILogStore logStore)
    {
        _logStore = logStore;
        ServiceNames = new ObservableCollection<string>(ServiceCatalog.All.Select(e => e.Name));
        _logStore.LineAppended += OnLineAppended;
        SelectedService = ServiceNames.FirstOrDefault();
    }

    /// <summary>Selects a service by name (used when navigating from a service row).</summary>
    public void SelectService(string name)
    {
        if (ServiceNames.Contains(name))
            SelectedService = name;
    }

    partial void OnSelectedServiceChanged(string? value) => ReloadLines();

    partial void OnFilterTextChanged(string value) => ReloadLines();

    private void ReloadLines()
    {
        Lines.Clear();
        if (SelectedService is not null)
        {
            foreach (var line in _logStore.Snapshot(SelectedService).Where(PassesFilter))
                Lines.Add(line);
        }
        LinesChanged?.Invoke();
    }

    private void OnLineAppended(string service, LogLine line)
    {
        if (!string.Equals(service, SelectedService, StringComparison.OrdinalIgnoreCase))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!string.Equals(service, SelectedService, StringComparison.OrdinalIgnoreCase) || !PassesFilter(line))
                return;
            Lines.Add(line);
            LinesChanged?.Invoke();
        });
    }

    private bool PassesFilter(LogLine line)
        => string.IsNullOrEmpty(FilterText) || line.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Clear()
    {
        if (SelectedService is null)
            return;
        _logStore.Clear(SelectedService);
        Lines.Clear();
        LinesChanged?.Invoke();
    }

    /// <summary>Builds the currently shown log as plain text (for copy/save).</summary>
    public string BuildText()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
            sb.Append(line.Timestamp.ToString("HH:mm:ss.fff")).Append("  ")
              .Append(line.Stream switch { LogStream.Stderr => "ERR ", LogStream.Info => "··· ", _ => "    " })
              .AppendLine(line.Text);
        return sb.ToString();
    }
}
