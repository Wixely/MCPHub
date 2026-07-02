using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.App.Proxy;
using MCPHub.AppHost;
using MCPHub.Proxy;

namespace MCPHub.App.ViewModels;

/// <summary>
/// Browses the proxy's aggregated catalog: every connected sub-MCP server and the tool calls it exposes
/// (by name), so you can confirm all servers and their tools are being aggregated correctly. Updates
/// live as upstreams connect/disconnect/re-list; a filter narrows to matching calls across all servers.
/// </summary>
public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly IUpstreamRegistry _registry;
    private readonly ProxyHost _host;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string? _summary;

    public ObservableCollection<DiagnosticsServerViewModel> Servers { get; } = [];

    public DiagnosticsViewModel(ProxyCoordinator coordinator)
    {
        _registry = coordinator.Registry;
        _host = coordinator.Host;
        _registry.CatalogChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public bool HasServers => Servers.Count > 0;

    partial void OnFilterTextChanged(string value) => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var catalog = _registry.Catalog;
        var upstreams = _registry.Upstreams.OrderBy(u => u.DisplayName).ToList();
        var filter = FilterText?.Trim() ?? string.Empty;
        var filtering = filter.Length > 0;

        Servers.Clear();

        // "MCP Proxy" pinned at the top: the complete set of calls the proxy advertises to clients.
        var proxyTools = catalog.Tools
            .Select(t => new DiagnosticsToolViewModel(t.Name, string.Empty, CleanDescription(t.Description)))
            .Where(t => !filtering || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!filtering || proxyTools.Count > 0)
            Servers.Add(DiagnosticsServerViewModel.ForProxy(_host.EndpointUrl, _host.IsRunning, proxyTools, expanded: filtering));

        // Then the real sub-servers, alphabetically.
        var connected = 0;
        foreach (var upstream in upstreams)
        {
            if (upstream.State == UpstreamState.Connected)
                connected++;

            var prefix = upstream.Key + ProxyConstants.NamespaceSeparator;
            var tools = catalog.Tools
                .Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(t => new DiagnosticsToolViewModel(t.Name[prefix.Length..], t.Name, CleanDescription(t.Description)))
                .Where(t => !filtering
                    || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || t.ExposedName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (filtering && tools.Count == 0)
                continue;

            Servers.Add(DiagnosticsServerViewModel.FromUpstream(upstream, tools, expanded: filtering));
        }

        Summary = $"{connected}/{upstreams.Count} servers connected · {catalog.Tools.Count} tool calls aggregated";
        OnPropertyChanged(nameof(HasServers));
    }

    // The catalog prefixes each description with "[ServiceLabel]"; drop it in this grouped-by-server view.
    private static string CleanDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return string.Empty;

        if (description.StartsWith('['))
        {
            var end = description.IndexOf(']');
            if (end >= 0)
                return description[(end + 1)..].TrimStart();
        }

        return description;
    }
}

/// <summary>One connected upstream server and the tool calls it exposes, for the Diagnostics tab.</summary>
public sealed class DiagnosticsServerViewModel
{
    private DiagnosticsServerViewModel(string displayName, string key, string endpoint, UpstreamState state,
        string? lastError, IReadOnlyList<DiagnosticsToolViewModel> tools, bool expanded)
    {
        DisplayName = displayName;
        Key = key;
        Endpoint = endpoint;
        State = state;
        StateText = state.ToString();
        LastError = lastError;
        Tools = tools;
        IsExpanded = expanded;
    }

    /// <summary>A real upstream sub-server and the calls it exposes.</summary>
    public static DiagnosticsServerViewModel FromUpstream(UpstreamServer upstream, IReadOnlyList<DiagnosticsToolViewModel> tools, bool expanded)
        => new(upstream.DisplayName, upstream.Key, upstream.Endpoint, upstream.State, upstream.LastError, tools, expanded);

    /// <summary>The MCPHub proxy itself — the union of every call it advertises to clients.</summary>
    public static DiagnosticsServerViewModel ForProxy(string endpoint, bool running, IReadOnlyList<DiagnosticsToolViewModel> tools, bool expanded)
        => new("MCP Proxy", "mcphub", endpoint,
            running ? UpstreamState.Connected : UpstreamState.Disconnected,
            running ? null : "Proxy is stopped — start it on the Proxy tab.",
            tools, expanded);

    public string DisplayName { get; }
    public string Key { get; }
    public string Endpoint { get; }
    public UpstreamState State { get; }
    public string StateText { get; }
    public string? LastError { get; }
    public bool HasError => !string.IsNullOrEmpty(LastError);
    public IReadOnlyList<DiagnosticsToolViewModel> Tools { get; }
    public bool IsExpanded { get; set; }
    public string ToolCountText => $"{Tools.Count} call{(Tools.Count == 1 ? "" : "s")}";
}

/// <summary>One exposed tool ("call") in the Diagnostics tab.</summary>
public sealed class DiagnosticsToolViewModel
{
    public DiagnosticsToolViewModel(string name, string exposedName, string description)
    {
        Name = name;
        ExposedName = exposedName;
        Description = description;
    }

    /// <summary>Original tool name on the upstream, e.g. <c>gh_list_issues</c>.</summary>
    public string Name { get; }

    /// <summary>Namespaced name clients call through the proxy, e.g. <c>github__gh_list_issues</c>.</summary>
    public string ExposedName { get; }

    public string Description { get; }
    public bool HasDescription => !string.IsNullOrEmpty(Description);
    public bool HasExposedName => !string.IsNullOrEmpty(ExposedName);
}
