using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MCPHub.App.Messages;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Services;

namespace MCPHub.App.ViewModels;

/// <summary>Observable wrapper around one <see cref="ManagedService"/> for the services list.</summary>
public sealed partial class ManagedServiceViewModel : ViewModelBase
{
    private readonly ManagedService _model;
    private readonly IServiceManager _manager;
    private readonly IServiceProcessHost _processHost;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    public ManagedServiceViewModel(ManagedService model, IServiceManager manager, IServiceProcessHost processHost)
    {
        _model = model;
        _manager = manager;
        _processHost = processHost;
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

    public string InstallButtonText => !_model.IsInstalled
        ? "Install"
        : _model.UpdateStatus == UpdateStatus.UpdateAvailable ? "Update" : "Reinstall";

    /// <summary>The config file edited by <see cref="EditConfigCommand"/>, e.g. <c>NoteworthyMCPSharp.json</c>.</summary>
    public string ConfigFileName => _model.Catalog.ConfigFileName;

    /// <summary>Whether a config file exists to edit (it ships with the install).</summary>
    public bool CanEditConfig => File.Exists(_model.ConfigPath);

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

    /// <summary>Starts the service hidden; run-state then advances via health probes.</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart)
            return;

        await _processHost.StartAsync(_model);
        SyncFromModel();
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

        IsInstalling = true;
        InstallProgress = 0;
        var progress = new Progress<double>(p => InstallProgress = p);
        try
        {
            await _manager.InstallOrUpdateAsync(_model, progress);
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
    }

    /// <summary>Opens this service's <c>{Name}.json</c> config in the OS default editor.</summary>
    [RelayCommand]
    private void EditConfig()
    {
        var path = _model.ConfigPath;
        if (!File.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open config '{path}': {ex.Message}");
        }
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
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(CanEditConfig));
    }
}
