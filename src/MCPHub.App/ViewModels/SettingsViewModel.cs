using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Models;
using MCPHub.Core.Services;
using MCPHub.Core.Settings;

namespace MCPHub.App.ViewModels;

/// <summary>Edits and persists MCPHub's settings (servers folder, flavour, proxy, tray, theme, PAT).</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly IServiceManager _manager;
    private readonly IAppPaths _paths;

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

    public SettingsViewModel(ISettingsStore settingsStore, ISecretStore secretStore, IServiceManager manager, IAppPaths paths)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _manager = manager;
        _paths = paths;

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
