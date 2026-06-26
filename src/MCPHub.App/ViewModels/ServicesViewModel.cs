using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Models;
using MCPHub.Core.Services;

namespace MCPHub.App.ViewModels;

/// <summary>Lists the managed MCP servers and drives the "check for updates" flow.</summary>
public sealed partial class ServicesViewModel : ViewModelBase
{
    private readonly IServiceManager _manager;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<ManagedServiceViewModel> Services { get; } = [];

    public ServicesViewModel(IServiceManager manager)
    {
        _manager = manager;

        foreach (var service in manager.Services)
            Services.Add(new ManagedServiceViewModel(service, manager));

        StatusMessage = $"Servers folder: {manager.ServersFolder}";
        _ = LoadInstalledAsync();
    }

    private async Task LoadInstalledAsync()
    {
        try
        {
            await _manager.RefreshInstalledAsync();
            foreach (var vm in Services)
                vm.SyncFromModel();
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to read installed versions: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task CheckAllUpdatesAsync()
    {
        if (IsChecking)
            return;

        IsChecking = true;
        StatusMessage = "Checking GitHub for the latest releases…";
        try
        {
            await _manager.RefreshInstalledAsync();

            var updatesAvailable = 0;
            var reachable = 0;
            foreach (var vm in Services)
            {
                await vm.CheckUpdateAsync();
                if (vm.LatestVersion != "—")
                    reachable++;
                if (vm.UpdateStatus == UpdateStatus.UpdateAvailable)
                    updatesAvailable++;
            }

            StatusMessage = reachable == 0
                ? "Couldn't reach GitHub — check your connection (or set MCPHUB_GITHUB_PAT to avoid rate limits)."
                : updatesAvailable > 0
                    ? $"{updatesAvailable} update(s) available across {reachable} services."
                    : $"Checked {reachable} services — installed copies are up to date.";
        }
        finally
        {
            IsChecking = false;
        }
    }
}
