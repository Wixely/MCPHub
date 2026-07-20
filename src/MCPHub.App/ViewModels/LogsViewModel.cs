using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Logging;

namespace MCPHub.App.ViewModels;

/// <summary>Shows a selected service's captured stdout/stderr, live-tailing new lines.</summary>
public sealed partial class LogsViewModel : ViewModelBase
{
    private readonly ILogStore _logStore;

    [ObservableProperty]
    private string? _selectedService;

    [ObservableProperty]
    private string _filterText = string.Empty;

    public ObservableCollection<string> ServiceNames { get; }

    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>Raised after lines change so the view can tail-scroll.</summary>
    public event Action? LinesChanged;

    public LogsViewModel(ILogStore logStore)
    {
        _logStore = logStore;

        // "MCPHub Proxy" is pinned at the top. A managed service is listed only once it has produced
        // output this session (started/faulted/etc.); services stay alphabetical below the proxy.
        ServiceNames = new ObservableCollection<string> { LogStoreLoggerProvider.ProxyLogKey };
        foreach (var service in logStore.Services
                     .Where(s => !string.Equals(s, LogStoreLoggerProvider.ProxyLogKey, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            ServiceNames.Add(service);

        _logStore.LineAppended += OnLineAppended;
        SelectedService = ServiceNames.FirstOrDefault();
    }

    /// <summary>Selects a service by name (used when navigating from a service row).</summary>
    public void SelectService(string name)
    {
        // A row's "Logs" action may target a service that just started — surface it if it has any output.
        if (!ServiceNames.Contains(name) && _logStore.Snapshot(name).Count > 0)
            EnsureListed(name);

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
        Dispatcher.UIThread.Post(() =>
        {
            EnsureListed(service);

            if (string.Equals(service, SelectedService, StringComparison.OrdinalIgnoreCase) && PassesFilter(line))
            {
                Lines.Add(line);
                LinesChanged?.Invoke();
            }
        });
    }

    /// <summary>Adds a service to the selector the first time it emits output — alphabetical, below the pinned proxy.</summary>
    private void EnsureListed(string service)
    {
        if (string.Equals(service, LogStoreLoggerProvider.ProxyLogKey, StringComparison.OrdinalIgnoreCase)
            || ServiceNames.Contains(service))
            return;

        var index = 1;
        while (index < ServiceNames.Count &&
               string.Compare(ServiceNames[index], service, StringComparison.OrdinalIgnoreCase) < 0)
            index++;
        ServiceNames.Insert(index, service);
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
