using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MCPHub.App.Messages;
using MCPHub.Core.Catalog;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Services;
using MCPHub.Core.Settings;

namespace MCPHub.App.ViewModels;

/// <summary>Observable wrapper around one <see cref="ManagedService"/> for the services list.</summary>
public sealed partial class ManagedServiceViewModel : ViewModelBase
{
    private readonly ManagedService _model;
    private readonly IServiceManager _manager;
    private readonly IServiceProcessHost _processHost;
    private readonly ISettingsStore _settings;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    /// <summary>True between the stop and the start of a restart, so the button can't be re-entered.</summary>
    [ObservableProperty]
    private bool _isRestarting;

    /// <summary>When checked, MCPHub starts this service automatically on launch.</summary>
    [ObservableProperty]
    private bool _autoRun;

    public ManagedServiceViewModel(ManagedService model, IServiceManager manager, IServiceProcessHost processHost, ISettingsStore settings)
    {
        _model = model;
        _manager = manager;
        _processHost = processHost;
        _settings = settings;
        _autoRun = settings.Current.AutoStartServices.Contains(Name);
        BuildConfigFiles();
    }

    partial void OnIsRestartingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartOrRestart));
        OnPropertyChanged(nameof(StartButtonText));
    }

    /// <summary>Persists the auto-run choice whenever the checkbox is toggled.</summary>
    partial void OnAutoRunChanged(bool value)
    {
        var list = _settings.Current.AutoStartServices;
        list.RemoveAll(n => string.Equals(n, Name, StringComparison.OrdinalIgnoreCase));
        if (value)
            list.Add(Name);
        _ = _settings.SaveAsync();
    }

    /// <summary>Starts the service at launch if it's flagged auto-run and currently able to start.</summary>
    public async Task StartForAutoRunAsync()
    {
        if (!AutoRun || !CanStart)
            return;

        await _processHost.StartAsync(_model);
        SyncFromModel();
    }

    public string Name => _model.Catalog.Name;
    public string DisplayName => _model.Catalog.DisplayName;
    public string Description => _model.Catalog.Description;
    public string RepositoryUrl => _model.Catalog.RepositoryUrl;
    public string PortText => _model.Port?.ToString() ?? "auto";
    public string InstalledVersion => _model.InstalledVersion ?? "—";
    public string LatestVersion => _model.LatestVersion ?? "—";
    public ServiceRunState RunState => _model.RunState;
    public UpdateStatus UpdateStatus => _model.UpdateStatus;

    public string RunStateText => _model.RunState.ToString();

    public string UpdateStatusText => _model.UpdateStatus switch
    {
        UpdateStatus.NotInstalled => "Not installed",
        UpdateStatus.UpToDate => "Up to date",
        UpdateStatus.UpdateAvailable => "Update available",
        _ => "—",
    };

    public bool CanStart => _model.IsInstalled && _model.RunState is ServiceRunState.Stopped or ServiceRunState.Faulted;

    public bool CanStop => _model.RunState is ServiceRunState.Starting or ServiceRunState.Running or ServiceRunState.Unhealthy;

    /// <summary>
    /// The primary run button is enabled whenever the service is installed and not mid-restart —
    /// it starts a stopped service and restarts a running one.
    /// </summary>
    public bool CanStartOrRestart => _model.IsInstalled && !IsRestarting && (CanStart || CanStop);

    /// <summary>"Restart" once the service is up, otherwise "Start".</summary>
    public string StartButtonText => IsRestarting ? "…" : CanStop ? "Restart" : "Start";

    public string InstallButtonText => !_model.IsInstalled
        ? "Install"
        : _model.UpdateStatus == UpdateStatus.UpdateAvailable ? "Update" : "Reinstall";

    /// <summary>The service's own config file, e.g. <c>NoteworthyMCPSharp.json</c>.</summary>
    public string ConfigFileName => _model.Catalog.ConfigFileName;

    /// <summary>Whether a config file exists to edit (it ships with the install).</summary>
    public bool CanEditConfig => File.Exists(_model.ConfigPath);

    /// <summary>True when this service reads more than one config file, so the button gets a menu.</summary>
    public bool HasExtraConfigs => _model.Catalog.HasExtraConfigFiles;

    /// <summary>The dropdown is available as soon as the service is installed — an extra config
    /// file that does not exist yet is created from its template when picked.</summary>
    public bool CanOpenConfigMenu => _model.IsInstalled;

    /// <summary>Every config file for this service, primary first. Empty unless <see cref="HasExtraConfigs"/>.</summary>
    public IReadOnlyList<ConfigFileViewModel> ConfigFiles { get; private set; } = [];

    private void BuildConfigFiles()
    {
        var catalog = _model.Catalog;
        if (!catalog.HasExtraConfigFiles)
        {
            ConfigFiles = [];
            return;
        }

        ConfigFiles = catalog.AllConfigFileNames
            .Select(f => new ConfigFileViewModel(
                f,
                ConfigFileResolver.DescribeFileName(f, catalog.Name),
                isPrimary: string.Equals(f, catalog.ConfigFileName, StringComparison.OrdinalIgnoreCase),
                OpenConfigFile))
            .ToList();
    }

    /// <summary>Checks GitHub for this service's latest release and refreshes the row.</summary>
    [RelayCommand]
    public async Task CheckUpdateAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await _manager.CheckForUpdatesAsync(_model);
        }
        finally
        {
            IsBusy = false;
            SyncFromModel();
        }
    }

    /// <summary>Populates Latest from the persisted release cache without any network call; true on a cache hit.</summary>
    public bool ApplyCachedLatest()
    {
        var hit = _manager.ApplyCachedLatest(_model);
        if (hit)
            SyncFromModel();
        return hit;
    }

    /// <summary>
    /// Starts a stopped service, or restarts a running one (stop, then start).
    ///
    /// Restarting is the common case after editing a config file, and doing it as Stop-then-Start
    /// left the row briefly looking idle and made a double-click easy — hence the single button and
    /// the <see cref="IsRestarting"/> guard.
    /// </summary>
    [RelayCommand]
    private async Task StartOrRestartAsync()
    {
        if (IsRestarting || !CanStartOrRestart)
            return;

        var wasRunning = CanStop;
        IsRestarting = true;
        try
        {
            if (wasRunning)
            {
                await _processHost.StopAsync(_model);
                SyncFromModel();
            }

            // A stop that faulted leaves the service unable to start; don't paper over it.
            if (!CanStart)
                return;

            await _processHost.StartAsync(_model);
        }
        finally
        {
            IsRestarting = false;
            SyncFromModel();
        }
    }

    /// <summary>Stops the running service.</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        if (!CanStop)
            return;

        await _processHost.StopAsync(_model);
        SyncFromModel();
    }

    /// <summary>Downloads and installs (or updates/reinstalls) this service, preserving config.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsInstalling)
            return;

        // The install stops a running instance to unlock its executable — capture that here so we can
        // bring it back up afterwards.
        var wasRunning = _processHost.IsRunning(Name);

        IsInstalling = true;
        InstallProgress = 0;
        var progress = new Progress<double>(p => InstallProgress = p);
        var installed = false;
        try
        {
            await _manager.InstallOrUpdateAsync(_model, progress);
            installed = true;
        }
        catch
        {
            // The failure reason is appended to this service's log by the install pipeline.
        }
        finally
        {
            IsInstalling = false;
            SyncFromModel();
        }

        // After a successful (re)install, restart the service if it was running before the update or is
        // flagged auto-run — otherwise an updated service sits Stopped and never returns to
        // Starting → Running (the "it never goes yellow again" symptom).
        if (installed && (wasRunning || AutoRun) && CanStart)
        {
            await _processHost.StartAsync(_model);
            SyncFromModel();
        }
    }

    /// <summary>Opens this service's own <c>{Name}.json</c> config in the OS default editor.</summary>
    [RelayCommand]
    private void EditConfig() => OpenConfigFile(_model.Catalog.ConfigFileName);

    /// <summary>
    /// Opens one of this service's config files, creating it from its shipped <c>.example</c>
    /// template if this is the first time it has been asked for.
    /// </summary>
    private void OpenConfigFile(string fileName)
    {
        var resolution = ConfigFileResolver.Resolve(_model.InstallFolder, fileName);
        if (!resolution.Exists || resolution.Path is null)
        {
            Debug.WriteLine($"No config file or template for '{fileName}' in '{_model.InstallFolder}'.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(resolution.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open config '{resolution.Path}': {ex.Message}");
        }

        // A promoted template is a new file on disk, so the primary-config check may have flipped.
        if (resolution.Outcome == ConfigFileOutcome.CreatedFromExample)
            OnPropertyChanged(nameof(CanEditConfig));
    }

    /// <summary>Switches to the Logs page focused on this service.</summary>
    [RelayCommand]
    private void ViewLogs() => WeakReferenceMessenger.Default.Send(new ShowLogsMessage(Name));

    /// <summary>Raises change notifications for every property backed by the underlying model.</summary>
    public void SyncFromModel()
    {
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(RunState));
        OnPropertyChanged(nameof(RunStateText));
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanStartOrRestart));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(CanEditConfig));
        OnPropertyChanged(nameof(CanOpenConfigMenu));
    }
}
