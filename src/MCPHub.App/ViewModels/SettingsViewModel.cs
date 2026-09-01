using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Management;
using MCPHub.Core.Models;
using MCPHub.Core.Services;
using MCPHub.Core.Settings;

namespace MCPHub.App.ViewModels;

/// <summary>Edits and persists MCPHub's settings (servers folder, flavour, proxy, tray, theme, PAT, agent management).</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly IServiceManager _manager;
    private readonly IAppPaths _paths;
    private readonly IAgentManagementPolicy _management;
    private bool _initialising;

    // Agent management switches. Unlike the rest of the page these persist as soon as they are toggled (no
    // Save needed) and take effect on the proxy immediately, matching the recipes checkboxes.
    [ObservableProperty] private bool _agentManagementEnabled;
    [ObservableProperty] private bool _agentControlEnabled;
    [ObservableProperty] private bool _agentInstallEnabled;
    [ObservableProperty] private bool _agentUpdateChecksEnabled;

    [ObservableProperty] private string _sharedServersFolder;
    [ObservableProperty] private bool _useSelfContained;
    [ObservableProperty] private string _proxyPortText;
    [ObservableProperty] private string _proxyBindAddress;
    [ObservableProperty] private bool _startProxyOnLaunch;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private string _theme;
    [ObservableProperty] private string _githubPatInput = string.Empty;
    [ObservableProperty] private bool _hasStoredPat;
    [ObservableProperty] private string? _statusMessage;

    public string[] Themes { get; } = ["Default", "Light", "Dark"];

    public SettingsViewModel(ISettingsStore settingsStore, ISecretStore secretStore, IServiceManager manager, IAppPaths paths, IAgentManagementPolicy management)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _manager = manager;
        _paths = paths;
        _management = management;

        _initialising = true;
        try
        {
            // Show the effective values (environment override included), not just what settings.json says.
            AgentManagementEnabled = management.ManagementEnabled;
            AgentControlEnabled = management.ControlSwitch;
            AgentInstallEnabled = management.InstallSwitch;
            AgentUpdateChecksEnabled = management.UpdateChecksSwitch;
        }
        finally
        {
            _initialising = false;
        }

        var s = settingsStore.Current;
        // Show the effective folder (the configured one, or the default) — never blank.
        _sharedServersFolder = string.IsNullOrWhiteSpace(s.SharedServersFolder)
            ? paths.DefaultServersDirectory
            : s.SharedServersFolder;
        _useSelfContained = s.Flavor == PublishFlavor.SelfContained;
        _proxyPortText = s.ProxyPort.ToString();
        _proxyBindAddress = s.ProxyBindAddress;
        _startProxyOnLaunch = s.StartProxyOnLaunch;
        _minimizeToTray = s.MinimizeToTray;
        _closeToTray = s.CloseToTray;
        _theme = s.Theme;
        _hasStoredPat = secretStore.Has(SecretKeys.GithubPat);
    }

    /// <summary>Set the servers folder from the folder picker (called by the view).</summary>
    public void SetFolder(string path) => SharedServersFolder = path;

    // ---- agent management -----------------------------------------------------------------------

    /// <summary>False when <c>MCPHUB_AGENT_MANAGEMENT_ENABLED</c> pins the value, so the checkbox is shown locked.</summary>
    public bool CanToggleAgentManagement => _management.ManagementEnabledOverrideSource is null;

    /// <summary>False while management is off (moot) or <c>MCPHUB_AGENT_MANAGEMENT_CONTROL</c> pins the value.</summary>
    public bool CanToggleAgentControl => AgentManagementEnabled && _management.ControlOverrideSource is null;

    /// <summary>False while management is off (moot) or <c>MCPHUB_AGENT_MANAGEMENT_INSTALL</c> pins the value.</summary>
    public bool CanToggleAgentInstall => AgentManagementEnabled && _management.InstallOverrideSource is null;

    /// <summary>False while management is off (moot) or <c>MCPHUB_AGENT_MANAGEMENT_UPDATE_CHECKS</c> pins the value.</summary>
    public bool CanToggleAgentUpdateChecks => AgentManagementEnabled && _management.UpdateChecksOverrideSource is null;

    /// <summary>One line describing what agents can currently do to MCPHub, plus any environment pin.</summary>
    public string AgentManagementSummary
    {
        get
        {
            string what;
            if (!_management.ManagementEnabled)
            {
                what = "Agents cannot manage servers — no mcphub__* tools are exposed.";
            }
            else
            {
                var can = new List<string>();
                if (_management.ControlEnabled) can.Add("start, stop and restart servers");
                if (_management.InstallEnabled) can.Add("install and update servers");
                if (_management.UpdateChecksEnabled) can.Add("check GitHub for server and MCPHub updates");
                what = can.Count == 0
                    ? "Agents can list servers (mcphub__list_services) but not change anything."
                    : $"Agents can list servers and {string.Join("; ", can)}.";
            }

            var pins = new[]
                {
                    _management.ManagementEnabledOverrideSource,
                    _management.ControlOverrideSource,
                    _management.InstallOverrideSource,
                    _management.UpdateChecksOverrideSource,
                }
                .Where(p => p is not null)
                .ToList();
            return pins.Count == 0
                ? what
                : $"{what} Pinned by the environment: {string.Join(", ", pins)} — change the container flag to alter it.";
        }
    }

    partial void OnAgentManagementEnabledChanged(bool value)
    {
        if (!_initialising)
        {
            _settingsStore.Current.AgentManagementEnabled = value;
            _ = _settingsStore.SaveAsync();
        }
        OnPropertyChanged(nameof(CanToggleAgentControl));
        OnPropertyChanged(nameof(CanToggleAgentInstall));
        OnPropertyChanged(nameof(CanToggleAgentUpdateChecks));
        OnPropertyChanged(nameof(AgentManagementSummary));
    }

    partial void OnAgentControlEnabledChanged(bool value)
    {
        if (!_initialising)
        {
            _settingsStore.Current.AgentManagementControlEnabled = value;
            _ = _settingsStore.SaveAsync();
        }
        OnPropertyChanged(nameof(AgentManagementSummary));
    }

    partial void OnAgentInstallEnabledChanged(bool value)
    {
        if (!_initialising)
        {
            _settingsStore.Current.AgentManagementInstallEnabled = value;
            _ = _settingsStore.SaveAsync();
        }
        OnPropertyChanged(nameof(AgentManagementSummary));
    }

    partial void OnAgentUpdateChecksEnabledChanged(bool value)
    {
        if (!_initialising)
        {
            _settingsStore.Current.AgentManagementUpdateChecksEnabled = value;
            _ = _settingsStore.SaveAsync();
        }
        OnPropertyChanged(nameof(AgentManagementSummary));
    }

    // ---- everything else ------------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveAsync()
    {
        var s = _settingsStore.Current;
        s.SharedServersFolder = SharedServersFolder?.Trim();
        s.Flavor = UseSelfContained ? PublishFlavor.SelfContained : PublishFlavor.FrameworkDependent;
        if (int.TryParse(ProxyPortText, out var port) && port is > 0 and < 65536)
            s.ProxyPort = port;
        s.ProxyBindAddress = string.IsNullOrWhiteSpace(ProxyBindAddress) ? "127.0.0.1" : ProxyBindAddress.Trim();
        s.StartProxyOnLaunch = StartProxyOnLaunch;
        s.MinimizeToTray = MinimizeToTray;
        s.CloseToTray = CloseToTray;
        s.Theme = Theme;
        await _settingsStore.SaveAsync();

        // Apply what we can without a restart.
        _manager.Flavor = s.Flavor;
        ApplyTheme(s.Theme);

        if (!string.IsNullOrWhiteSpace(GithubPatInput))
        {
            _secretStore.Set(SecretKeys.GithubPat, GithubPatInput.Trim());
            GithubPatInput = string.Empty;
            HasStoredPat = true;
        }

        StatusMessage = "Saved. Servers-folder and proxy-port changes take effect on restart.";
    }

    /// <summary>Opens the effective shared servers folder in the OS file manager.</summary>
    [RelayCommand]
    private void OpenServersFolder()
    {
        var path = string.IsNullOrWhiteSpace(SharedServersFolder)
            ? _paths.DefaultServersDirectory
            : SharedServersFolder.Trim();

        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("explorer.exe");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't open the folder: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearToken()
    {
        _secretStore.Set(SecretKeys.GithubPat, null);
        GithubPatInput = string.Empty;
        HasStoredPat = false;
        StatusMessage = "GitHub token cleared.";
    }

    /// <summary>Applies a theme variant immediately to the running application.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
    }
}
