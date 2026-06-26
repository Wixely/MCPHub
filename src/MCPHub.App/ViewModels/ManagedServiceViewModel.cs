using CommunityToolkit.Mvvm.ComponentModel;
using MCPHub.Core.Catalog;
using MCPHub.Core.Models;

namespace MCPHub.App.ViewModels;

/// <summary>Observable wrapper around one catalog product for display in the services list.</summary>
public sealed partial class ManagedServiceViewModel : ViewModelBase
{
    private readonly ServiceCatalogEntry _entry;

    public ManagedServiceViewModel(ServiceCatalogEntry entry) => _entry = entry;

    public string Name => _entry.Name;
    public string DisplayName => _entry.DisplayName;
    public string Description => _entry.Description;
    public string PortText => _entry.DefaultPort?.ToString() ?? "auto";
    public string RepositoryUrl => _entry.RepositoryUrl;

    [ObservableProperty] private ServiceRunState _runState = ServiceRunState.Stopped;
    [ObservableProperty] private UpdateStatus _updateStatus = UpdateStatus.Unknown;
    [ObservableProperty] private string _installedVersion = "—";
    [ObservableProperty] private string _latestVersion = "—";
}
