using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.App.Proxy;
using MCPHub.AppHost;
using MCPHub.Proxy;

namespace MCPHub.App.ViewModels;

/// <summary>Status and controls for MCPHub's aggregated MCP proxy endpoint.</summary>
public sealed partial class ProxyViewModel : ViewModelBase
{
    private readonly ProxyHost _host;
    private readonly IUpstreamRegistry _registry;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private int _toolCount;

    public ObservableCollection<UpstreamRowViewModel> Upstreams { get; } = [];

    public ProxyViewModel(ProxyCoordinator coordinator)
    {
        _host = coordinator.Host;
        _registry = coordinator.Registry;
        _registry.CatalogChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public string EndpointUrl => _host.EndpointUrl;

    public string ClientSnippet =>
        "{\n" +
        "  \"mcpServers\": {\n" +
        "    \"mcphub\": {\n" +
        $"      \"url\": \"{_host.EndpointUrl}\"\n" +
        "    }\n" +
        "  }\n" +
        "}";

    public string ToggleButtonText => IsRunning ? "Stop proxy" : "Start proxy";

    public bool HasUpstreams => Upstreams.Count > 0;

    private void Refresh()
    {
        IsRunning = _host.IsRunning;
        ToolCount = _registry.Catalog.Tools.Count;

        Upstreams.Clear();
        foreach (var upstream in _registry.Upstreams.OrderBy(u => u.DisplayName))
            Upstreams.Add(new UpstreamRowViewModel(upstream));

        OnPropertyChanged(nameof(EndpointUrl));
        OnPropertyChanged(nameof(ClientSnippet));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(HasUpstreams));
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_host.IsRunning)
            await _host.StopAsync();
        else
            await _host.StartAsync(_host.BindAddress, _host.Port);

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
        Endpoint = upstream.Endpoint.ToString();
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
