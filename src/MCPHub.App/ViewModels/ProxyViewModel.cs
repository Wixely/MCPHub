using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.App.Proxy;
using MCPHub.Hosting;
using MCPHub.Core.Settings;
using MCPHub.Proxy;

namespace MCPHub.App.ViewModels;

/// <summary>Status and controls for MCPHub's aggregated MCP proxy endpoint, plus user-added servers.</summary>
public sealed partial class ProxyViewModel : ViewModelBase
{
    private readonly ProxyCoordinator _coordinator;
    private readonly ProxyHost _host;
    private readonly IUpstreamRegistry _registry;
    private readonly ProxyHandlers _handlers;
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _toolCount;
    [ObservableProperty] private string _newServerName = string.Empty;
    [ObservableProperty] private bool _newServerIsStdio;
    [ObservableProperty] private string _newServerTarget = string.Empty;
    [ObservableProperty] private string _newServerToken = string.Empty;
    [ObservableProperty] private string _newServerAuthName = string.Empty;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<UpstreamRowViewModel> Upstreams { get; } = [];
    public ObservableCollection<UserServerRowViewModel> UserServers { get; } = [];

    public ProxyViewModel(ProxyCoordinator coordinator, ProxyHandlers handlers, ISettingsStore settings, ISecretStore secrets)
    {
        _coordinator = coordinator;
        _host = coordinator.Host;
        _registry = coordinator.Registry;
        _handlers = handlers;
        _settings = settings;
        _secrets = secrets;
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

    /// <summary>Hint for the optional header/variable box, which changes meaning with the transport.</summary>
    public string NewServerAuthNameWatermark => NewServerIsStdio
        ? $"Env var (default {UserMcpServerDefinition.DefaultAuthEnvironmentVariable})"
        : "Header (default Authorization: Bearer)";

    private void Refresh()
    {
        IsRunning = _host.IsRunning;
        ToolCount = _handlers.ListTools(TenantContext.Default).Tools.Count; // upstream + in-process (recipes) tools

        Upstreams.Clear();
        foreach (var upstream in _registry.Upstreams.OrderBy(u => u.DisplayName))
            Upstreams.Add(new UpstreamRowViewModel(upstream));

        // Rows are rebuilt on every catalog change — including the reconnect sweep — so an open token
        // editor has to survive the rebuild, or a token being pasted disappears under the user.
        var editing = UserServers
            .Where(r => r.IsEditingToken)
            .ToDictionary(r => r.Id, r => r.TokenInput, StringComparer.Ordinal);

        UserServers.Clear();
        foreach (var definition in _settings.Current.UserServers)
        {
            var row = new UserServerRowViewModel(definition, this, _secrets.Has(definition.SecretKey));
            if (editing.TryGetValue(definition.Id, out var inProgress))
            {
                row.TokenInput = inProgress;
                row.IsEditingToken = true;
            }

            UserServers.Add(row);
        }

        OnPropertyChanged(nameof(EndpointUrl));
        OnPropertyChanged(nameof(ClientSnippet));
        OnPropertyChanged(nameof(ToggleButtonText));
        OnPropertyChanged(nameof(HasUpstreams));
    }

    partial void OnNewServerIsStdioChanged(bool value)
    {
        OnPropertyChanged(nameof(NewServerTargetWatermark));
        OnPropertyChanged(nameof(NewServerAuthNameWatermark));
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

        ApplyAuthChoice(definition);

        _settings.Current.UserServers.Add(definition);
        await _settings.SaveAsync();

        // The token goes to the secret store only — it is never written to settings.json.
        if (definition.Auth != McpAuthKind.None)
            _secrets.Set(definition.SecretKey, NewServerToken.Trim());

        NewServerName = string.Empty;
        NewServerTarget = string.Empty;
        NewServerToken = string.Empty;
        NewServerAuthName = string.Empty;
        StatusMessage = $"Added '{definition.DisplayName}'.";

        await _coordinator.RefreshUserServersAsync();
        Refresh();
    }

    /// <summary>
    /// Chooses how the entered token is delivered: stdio gets an environment variable, HTTP gets a bearer
    /// header unless the user named a header to use instead.
    /// </summary>
    private void ApplyAuthChoice(UserMcpServerDefinition definition)
    {
        var authName = NewServerAuthName.Trim();

        if (string.IsNullOrWhiteSpace(NewServerToken))
        {
            definition.Auth = McpAuthKind.None;
            return;
        }

        if (definition.Kind == McpTransportKind.Stdio)
        {
            definition.Auth = McpAuthKind.EnvironmentToken;
            definition.AuthEnvironmentVariable = authName.Length > 0 ? authName : null;
            return;
        }

        definition.Auth = authName.Length > 0 ? McpAuthKind.HeaderToken : McpAuthKind.BearerToken;
        definition.AuthHeaderName = authName.Length > 0 ? authName : null;
    }

    public async Task RemoveUserServerAsync(UserServerRowViewModel row)
    {
        // Drop the token with the server, so removing it does not leave a credential behind in the store.
        var definition = _settings.Current.UserServers.FirstOrDefault(d => d.Id == row.Id);
        if (definition is not null)
            _secrets.Set(definition.SecretKey, null);

        _settings.Current.UserServers.RemoveAll(d => d.Id == row.Id);
        await _settings.SaveAsync();
        await _coordinator.RefreshUserServersAsync();
        Refresh();
    }

    /// <summary>Replaces the stored token for an existing server and reconnects it with the new credential.</summary>
    public async Task SetUserServerTokenAsync(UserServerRowViewModel row, string token)
    {
        var definition = _settings.Current.UserServers.FirstOrDefault(d => d.Id == row.Id);
        if (definition is null)
            return;

        token = token.Trim();
        if (token.Length == 0)
        {
            StatusMessage = "Enter a token first, or use Clear to remove the current one.";
            return;
        }

        // A server with no delivery mechanism yet gets its transport's default — but a header or variable
        // name configured earlier still wins, so clearing and re-setting a token keeps the same scheme.
        if (definition.Auth == McpAuthKind.None)
            definition.Auth = definition.Kind switch
            {
                McpTransportKind.Stdio => McpAuthKind.EnvironmentToken,
                _ when !string.IsNullOrWhiteSpace(definition.AuthHeaderName) => McpAuthKind.HeaderToken,
                _ => McpAuthKind.BearerToken,
            };

        _secrets.Set(definition.SecretKey, token);
        await _settings.SaveAsync();
        StatusMessage = $"Token updated for '{definition.DisplayName}'.";

        await _coordinator.RefreshUserServersAsync();
        Refresh();
    }

    /// <summary>Removes an existing server's token and reconnects it unauthenticated.</summary>
    public async Task ClearUserServerTokenAsync(UserServerRowViewModel row)
    {
        var definition = _settings.Current.UserServers.FirstOrDefault(d => d.Id == row.Id);
        if (definition is null)
            return;

        _secrets.Set(definition.SecretKey, null);
        definition.Auth = McpAuthKind.None;
        await _settings.SaveAsync();
        StatusMessage = $"Token cleared for '{definition.DisplayName}'.";

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

/// <summary>One user-added server row, with its own remove/toggle and token commands.</summary>
public sealed partial class UserServerRowViewModel : ViewModelBase
{
    private readonly ProxyViewModel _parent;

    [ObservableProperty] private bool _isEditingToken;
    [ObservableProperty] private string _tokenInput = string.Empty;

    public UserServerRowViewModel(UserMcpServerDefinition definition, ProxyViewModel parent, bool hasToken)
    {
        _parent = parent;
        Id = definition.Id;
        DisplayName = definition.DisplayName;
        Target = definition.Kind == McpTransportKind.Http ? definition.Endpoint ?? string.Empty : $"stdio: {definition.Command}";
        Enabled = definition.Enabled;
        ToggleText = definition.Enabled ? "Disable" : "Enable";
        HasToken = hasToken;
        AuthSummary = DescribeAuth(definition, hasToken);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Target { get; }
    public bool Enabled { get; }
    public string ToggleText { get; }

    /// <summary>Whether a token is currently stored for this server.</summary>
    public bool HasToken { get; }

    /// <summary>How this server authenticates, for the row subtitle. Never includes the token itself.</summary>
    public string AuthSummary { get; }

    public string SetTokenButtonText => HasToken ? "Replace token" : "Set token";

    /// <summary>
    /// Names the credential's delivery mechanism without revealing it. A configured mechanism with no stored
    /// token is called out, since that combination silently connects unauthenticated.
    /// </summary>
    private static string DescribeAuth(UserMcpServerDefinition definition, bool hasToken) => definition.Auth switch
    {
        McpAuthKind.None => "No token",
        _ when !hasToken => $"{definition.Auth} configured — token missing",
        McpAuthKind.BearerToken => "Authorization: Bearer ••••",
        McpAuthKind.HeaderToken => $"{definition.EffectiveAuthHeaderName}: ••••",
        McpAuthKind.EnvironmentToken => $"{definition.EffectiveAuthEnvironmentVariable}=••••",
        _ => "No token",
    };

    [RelayCommand]
    private Task Remove() => _parent.RemoveUserServerAsync(this);

    [RelayCommand]
    private Task Toggle() => _parent.ToggleUserServerAsync(this);

    [RelayCommand]
    private void BeginEditToken()
    {
        TokenInput = string.Empty;
        IsEditingToken = true;
    }

    [RelayCommand]
    private void CancelEditToken()
    {
        TokenInput = string.Empty;
        IsEditingToken = false;
    }

    [RelayCommand]
    private async Task SaveToken()
    {
        var token = TokenInput;
        TokenInput = string.Empty;
        IsEditingToken = false;
        await _parent.SetUserServerTokenAsync(this, token);
    }

    [RelayCommand]
    private Task ClearToken() => _parent.ClearUserServerTokenAsync(this);
}
