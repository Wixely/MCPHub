using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.App.Proxy;
using MCPHub.AppHost;
using MCPHub.Core.Settings;
using MCPHub.Proxy;

namespace MCPHub.App.ViewModels;

/// <summary>Status and controls for MCPHub's aggregated MCP proxy endpoint, plus user-added servers.</summary>
public sealed partial class ProxyViewModel : ViewModelBase
{
    private readonly ProxyCoordinator _coordinator;
    private readonly ProxyHost _host;
    private readonly IUpstreamRegistry _registry;
    private readonly ISettingsStore _settings;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _toolCount;
    [ObservableProperty] private string _newServerName = string.Empty;
    [ObservableProperty] private bool _newServerIsStdio;
    [ObservableProperty] private string _newServerTarget = string.Empty;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<UpstreamRowViewModel> Upstreams { get; } = [];
    public ObservableCollection<UserServerRowViewModel> UserServers { get; } = [];

    public ProxyViewModel(ProxyCoordinator coordinator, ISettingsStore settings)
    {
        _coordinator = coordinator;
        _host = coordinator.Host;
        _registry = coordinator.Registry;
        _settings = settings;
        _registry.CatalogChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public string EndpointUrl => _host.EndpointUrl;

    public string ClientSnippet =>
        "{\n  \"mcpServers\": {\n    \"mcphub\": {\n" +
        $"      \"url\": \"{_host.EndpointUrl}\"\n" +
        "    }\n  }\n}";

    public string ToggleButtonText => IsRunning ? "Stop proxy" : "Start proxy";

    public bool HasUpstreams => Upstreams.Count > 0;

    public string NewServerTargetWatermark => NewServerIsStdio ? "command arg1 arg2…" : "http://localhost:1234/mcp";

    private void Refresh()
    {
        IsRunning = _host.IsRunning;
        ToolCount = _registry.Catalog.Tools.Count;

        Upstreams.Clear();
        foreach (var upstream in _registry.Upstreams.OrderBy(u => u.DisplayName))
            Upstreams.Add(new UpstreamRowViewModel(upstream));

        UserServers.Clear();
        foreach (var definition in _settings.Current.UserServers)
            UserServers.Add(new UserServerRowViewModel(definition, this));

        OnPropertyChanged(nameof(EndpointUrl));
        OnPropertyChanged(nameof(ClientSnippet));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(HasUpstreams));
    }

    partial void OnNewServerIsStdioChanged(bool value) => OnPropertyChanged(nameof(NewServerTargetWatermark));

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_host.IsRunning)
            await _host.StopAsync();
        else
            await _host.StartAsync(_host.BindAddress, _host.Port);

        Refresh();
    }

    [RelayCommand]
    private async Task AddUserServerAsync()
    {
        if (string.IsNullOrWhiteSpace(NewServerTarget))
        {
            StatusMessage = "Enter an endpoint URL or a command first.";
            return;
        }

        var definition = new UserMcpServerDefinition
        {
            DisplayName = string.IsNullOrWhiteSpace(NewServerName) ? NewServerTarget.Trim() : NewServerName.Trim(),
            Kind = NewServerIsStdio ? McpTransportKind.Stdio : McpTransportKind.Http,
            Enabled = true,
        };

        if (NewServerIsStdio)
        {
            var parts = NewServerTarget.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            definition.Command = parts.FirstOrDefault();
            definition.Arguments = parts.Skip(1).ToList();
        }
        else
        {
            definition.Endpoint = NewServerTarget.Trim();
        }

        _settings.Current.UserServers.Add(definition);
        await _settings.SaveAsync();

        NewServerName = string.Empty;
        NewServerTarget = string.Empty;
        StatusMessage = $"Added '{definition.DisplayName}'.";

        await _coordinator.RefreshUserServersAsync();
        Refresh();
    }

    public async Task RemoveUserServerAsync(UserServerRowViewModel row)
    {
        _settings.Current.UserServers.RemoveAll(d => d.Id == row.Id);
        await _settings.SaveAsync();
        await _coordinator.RefreshUserServersAsync();
        Refresh();
    }

    public async Task ToggleUserServerAsync(UserServerRowViewModel row)
    {
        var definition = _settings.Current.UserServers.FirstOrDefault(d => d.Id == row.Id);
        if (definition is null)
            return;

        definition.Enabled = !definition.Enabled;
        await _settings.SaveAsync();
        await _coordinator.RefreshUserServersAsync();
        Refresh();
    }
}

/// <summary>One upstream row in the proxy status list.</summary>
public sealed class UpstreamRowViewModel
{
    public UpstreamRowViewModel(UpstreamServer upstream)
    {
        DisplayName = upstream.DisplayName;
        State = upstream.State;
        StateText = upstream.State.ToString();
        ToolCount = upstream.ToolCount;
        Endpoint = upstream.Endpoint;
        LastError = upstream.LastError;
    }

    public string DisplayName { get; }
    public UpstreamState State { get; }
    public string StateText { get; }
    public int ToolCount { get; }
    public string Endpoint { get; }
    public string? LastError { get; }
    public bool HasError => !string.IsNullOrEmpty(LastError);
}

/// <summary>One user-added server row, with its own remove/toggle commands.</summary>
public sealed partial class UserServerRowViewModel : ViewModelBase
{
    private readonly ProxyViewModel _parent;

    public UserServerRowViewModel(UserMcpServerDefinition definition, ProxyViewModel parent)
    {
        _parent = parent;
        Id = definition.Id;
        DisplayName = definition.DisplayName;
        Target = definition.Kind == McpTransportKind.Http ? definition.Endpoint ?? string.Empty : $"stdio: {definition.Command}";
        Enabled = definition.Enabled;
        ToggleText = definition.Enabled ? "Disable" : "Enable";
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Target { get; }
    public bool Enabled { get; }
    public string ToggleText { get; }

    [RelayCommand]
    private Task Remove() => _parent.RemoveUserServerAsync(this);

    [RelayCommand]
    private Task Toggle() => _parent.ToggleUserServerAsync(this);
}
