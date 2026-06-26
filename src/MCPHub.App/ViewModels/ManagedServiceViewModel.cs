using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Models;
using MCPHub.Core.Services;

namespace MCPHub.App.ViewModels;

/// <summary>Observable wrapper around one <see cref="ManagedService"/> for the services list.</summary>
public sealed partial class ManagedServiceViewModel : ViewModelBase
{
    private readonly ManagedService _model;
    private readonly IServiceManager _manager;

    [ObservableProperty]
    private bool _isBusy;

    public ManagedServiceViewModel(ManagedService model, IServiceManager manager)
    {
        _model = model;
        _manager = manager;
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

    public string UpdateStatusText => _model.UpdateStatus switch
    {
        UpdateStatus.NotInstalled => "Not installed",
        UpdateStatus.UpToDate => "Up to date",
        UpdateStatus.UpdateAvailable => "Update available",
        _ => "—",
    };

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

    /// <summary>Raises change notifications for every property backed by the underlying model.</summary>
    public void SyncFromModel()
    {
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(RunState));
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(UpdateStatusText));
    }
}
