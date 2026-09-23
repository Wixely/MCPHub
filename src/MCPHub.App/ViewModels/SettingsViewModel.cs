using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.App.Proxy;
using MCPHub.Core.Backup;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Management;
using MCPHub.Core.Models;
using MCPHub.Core.Services;
using MCPHub.Core.Settings;

namespace MCPHub.App.ViewModels;

/// <summary>One export/import category with its tick box state and a description of what it moves.</summary>
public sealed partial class SettingsCategoryOption : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    /// <summary>Whether the archive being imported actually contains this category.</summary>
    [ObservableProperty] private bool _isAvailable = true;

    public SettingsCategoryOption(SettingsCategory category, string title, string description, bool selected)
    {
        Category = category;
        Title = title;
        Description = description;
        _isSelected = selected;
    }

    public SettingsCategory Category { get; }
    public string Title { get; }
    public string Description { get; }
}

/// <summary>Edits and persists MCPHub's settings (servers folder, flavour, proxy, tray, theme, PAT, agent management).</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISecretStore _secretStore;
    private readonly IServiceManager _manager;
    private readonly IAppPaths _paths;
    private readonly IAgentManagementPolicy _management;
    private readonly ISettingsArchiveService _archive;
    private readonly ProxyCoordinator _proxy;
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

    // Import / export.
    [ObservableProperty] private string _archivePassword = string.Empty;
    [ObservableProperty] private string? _archiveStatus;
    [ObservableProperty] private bool _isArchiveBusy;

    public string[] Themes { get; } = ["Default", "Light", "Dark"];

    /// <summary>The categories offered for export and import, in the order they are shown.</summary>
    public ObservableCollection<SettingsCategoryOption> ArchiveCategories { get; } =
    [
        new(SettingsCategory.General, "General and proxy",
            "Servers folder, download flavour, proxy port and bind address, launch and tray behaviour.", true),
        new(SettingsCategory.UserServers, "User-added MCP servers",
            "The servers you added on the Proxy page. Their tokens travel only with Tokens and keys.", true),
        new(SettingsCategory.Router, "Model Router routes",
            "Listener settings, model outputs and agents, including existing agent keys. Upstream provider keys travel only with Tokens and keys.", true),
        new(SettingsCategory.Recipes, "Recipes",
            "The recipes knowledge base. Importing merges by id and keeps recipes the archive does not mention.", true),
        new(SettingsCategory.AgentAndEngine, "Agent and engine",
            "DaggerAgent and Slopworks folders, auto-start choices, and the agent-management switches.", true),
        new(SettingsCategory.Appearance, "Appearance",
            "Theme and remembered window size.", false),
        new(SettingsCategory.Secrets, "Tokens and keys",
            "GitHub token, MCP server tokens and Router upstream keys. Requires a password — they are re-encrypted into the archive.", false),
    ];

    public SettingsViewModel(
        ISettingsStore settingsStore,
        ISecretStore secretStore,
        IServiceManager manager,
        IAppPaths paths,
        IAgentManagementPolicy management,
        ISettingsArchiveService archive,
        ProxyCoordinator proxy)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _manager = manager;
        _paths = paths;
        _management = management;
        _archive = archive;
        _proxy = proxy;

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

    /// <summary>The folder actually in use: the configured one, or the default when none is set.</summary>
    private string EffectiveServersFolder(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? _paths.DefaultServersDirectory : configured.Trim();

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
        // Compared as effective paths: the box shows the default when nothing is configured, so comparing the
        // raw setting would report a change the first time anyone presses Save.
        var previousFolder = EffectiveServersFolder(s.SharedServersFolder);
        var previousBind = s.ProxyBindAddress;
        var previousPort = s.ProxyPort;

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

        var endpointChanged = previousBind != s.ProxyBindAddress || previousPort != s.ProxyPort;
        var proxyNote = endpointChanged ? await RebindProxyAsync(s) : string.Empty;
        var folderNote = !string.Equals(previousFolder, EffectiveServersFolder(s.SharedServersFolder), StringComparison.OrdinalIgnoreCase)
            ? " The servers folder applies to installs made from now on; restart MCPHub to move running servers to it."
            : string.Empty;

        StatusMessage = "Saved." + proxyNote + folderNote;
    }

    /// <summary>
    /// Moves the running proxy onto a newly-saved address and port. A listening socket cannot be re-pointed,
    /// so the endpoint is stopped and started; upstream servers stay connected throughout, and connected
    /// agents simply reconnect to the new URL. Failure leaves the proxy stopped and says so rather than
    /// pretending the save did not happen — the setting is already on disk.
    /// </summary>
    private async Task<string> RebindProxyAsync(MCPHubSettings s)
    {
        var host = _proxy.Host;
        if (!host.IsRunning)
        {
            host.Configure(s.ProxyBindAddress, s.ProxyPort);
            return " The proxy will use the new address and port when it starts.";
        }

        try
        {
            await host.RestartAsync(s.ProxyBindAddress, s.ProxyPort);
            return $" The proxy moved to {host.EndpointUrl} — update your clients' MCP URL.";
        }
        catch (Exception ex)
        {
            return $" The proxy could not bind {s.ProxyBindAddress}:{s.ProxyPort} ({ex.Message}) and is now stopped. " +
                   "Correct the address or port and save again, or start it from the Proxy page.";
        }
    }

    /// <summary>Opens <c>settings.json</c> in whatever the OS uses for the file type.</summary>
    [RelayCommand]
    private async Task OpenConfigFileAsync()
    {
        // The file may not exist yet on a first run, and "open" on a missing path fails obscurely.
        var path = Path.Combine(_paths.SettingsDirectory, "settings.json");
        if (!File.Exists(path))
            await _settingsStore.SaveAsync();

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusMessage = "Opened settings.json. Changes made there are read when MCPHub next starts — and are overwritten if you press Save here first.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open {path}: {ex.Message}";
        }
    }

    /// <summary>Opens the folder holding settings.json, secrets.json, router.json and recipes.json.</summary>
    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.SettingsDirectory);
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("explorer.exe");
                psi.ArgumentList.Add(_paths.SettingsDirectory);
                Process.Start(psi);
            }
            else
            {
                Process.Start(new ProcessStartInfo(_paths.SettingsDirectory) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't open the config folder: " + ex.Message;
        }
    }

    // ---- import / export ------------------------------------------------------------------------

    /// <summary>Default file name offered by the save dialog.</summary>
    public static string SuggestedArchiveName => $"mcphub-settings-{DateTime.Now:yyyyMMdd-HHmm}.zip";

    /// <summary>Categories currently ticked. Unavailable ones (absent from an archive) are never included.</summary>
    private IReadOnlyCollection<SettingsCategory> SelectedCategories =>
        [.. ArchiveCategories.Where(c => c.IsSelected && c.IsAvailable).Select(c => c.Category)];

    /// <summary>Writes the ticked categories to <paramref name="path"/>; called by the view after its file dialog.</summary>
    public async Task ExportToAsync(string path)
    {
        IsArchiveBusy = true;
        try
        {
            var chosen = SelectedCategories;
            var password = ArchivePassword.Trim();
            await _archive.ExportAsync(path, chosen, password.Length == 0 ? null : password);
            ArchiveStatus = $"Exported {chosen.Count} categor{(chosen.Count == 1 ? "y" : "ies")} to {Path.GetFileName(path)}" +
                (password.Length == 0 ? " (not encrypted — it contains no tokens or keys)." : ", encrypted with your password.");
        }
        catch (SettingsArchiveException ex) { ArchiveStatus = ex.Message; }
        catch (Exception ex) { ArchiveStatus = "Export failed: " + ex.Message; }
        finally { IsArchiveBusy = false; }
    }

    /// <summary>
    /// Reads an archive's manifest and ticks only what it holds, so the user chooses from what is actually
    /// there. Called by the view after picking a file, before <see cref="ImportFromAsync"/>.
    /// </summary>
    public async Task<bool> InspectArchiveAsync(string path)
    {
        IsArchiveBusy = true;
        try
        {
            var manifest = await _archive.InspectAsync(path);
            foreach (var option in ArchiveCategories)
            {
                option.IsAvailable = manifest.Categories.Contains(option.Category);
                option.IsSelected = option.IsAvailable;
            }

            ArchiveStatus = $"{Path.GetFileName(path)} holds {manifest.Categories.Length} categor" +
                $"{(manifest.Categories.Length == 1 ? "y" : "ies")}, written {manifest.CreatedUtc.ToLocalTime():g} by MCPHub {manifest.AppVersion}." +
                (manifest.Encrypted ? " It is encrypted — enter its password before importing." : string.Empty);
            return true;
        }
        catch (SettingsArchiveException ex) { ArchiveStatus = ex.Message; return false; }
        catch (Exception ex) { ArchiveStatus = "Could not read that archive: " + ex.Message; return false; }
        finally { IsArchiveBusy = false; }
    }

    /// <summary>Applies the ticked categories from <paramref name="path"/> and reports what changed.</summary>
    public async Task ImportFromAsync(string path)
    {
        IsArchiveBusy = true;
        try
        {
            var previousBind = _settingsStore.Current.ProxyBindAddress;
            var previousPort = _settingsStore.Current.ProxyPort;

            var password = ArchivePassword.Trim();
            var result = await _archive.ImportAsync(path, SelectedCategories, password.Length == 0 ? null : password);

            ReloadFromSettings();
            ApplyTheme(_settingsStore.Current.Theme);
            _manager.Flavor = _settingsStore.Current.Flavor;

            // An imported proxy endpoint is applied here for the same reason Save applies one: the setting
            // is already stored, so leaving the live proxy on the old address would be the surprising half.
            var s = _settingsStore.Current;
            var proxyNote = previousBind != s.ProxyBindAddress || previousPort != s.ProxyPort
                ? await RebindProxyAsync(s)
                : string.Empty;

            // Imported servers replaced the list wholesale, so reconnect from the new one either way.
            await _proxy.RefreshUserServersAsync();

            var restart = result.RequiresRestartFor.Count == 0
                ? string.Empty
                : " Needs a restart or a manual apply: " + string.Join("; ", result.RequiresRestartFor) + ".";
            ArchiveStatus = string.Join(" ", result.Notes) + proxyNote + restart;
        }
        catch (SettingsArchiveException ex) { ArchiveStatus = ex.Message; }
        catch (Exception ex) { ArchiveStatus = "Import failed: " + ex.Message; }
        finally { IsArchiveBusy = false; }
    }

    /// <summary>Re-reads the page's fields after an import replaced the underlying settings.</summary>
    private void ReloadFromSettings()
    {
        var s = _settingsStore.Current;
        _initialising = true;
        try
        {
            SharedServersFolder = string.IsNullOrWhiteSpace(s.SharedServersFolder) ? _paths.DefaultServersDirectory : s.SharedServersFolder;
            UseSelfContained = s.Flavor == PublishFlavor.SelfContained;
            ProxyPortText = s.ProxyPort.ToString();
            ProxyBindAddress = s.ProxyBindAddress;
            StartProxyOnLaunch = s.StartProxyOnLaunch;
            MinimizeToTray = s.MinimizeToTray;
            CloseToTray = s.CloseToTray;
            Theme = s.Theme;
            AgentManagementEnabled = _management.ManagementEnabled;
            AgentControlEnabled = _management.ControlSwitch;
            AgentInstallEnabled = _management.InstallSwitch;
            AgentUpdateChecksEnabled = _management.UpdateChecksSwitch;
        }
        finally
        {
            _initialising = false;
        }

        HasStoredPat = _secretStore.Has(SecretKeys.GithubPat);
        OnPropertyChanged(nameof(AgentManagementSummary));
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
