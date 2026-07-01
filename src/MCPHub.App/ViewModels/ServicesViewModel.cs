using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;

namespace MCPHub.App.ViewModels;

/// <summary>Lists the managed MCP servers and drives the check-for-updates and start/stop flows.</summary>
public sealed partial class ServicesViewModel : ViewModelBase
{
    private readonly IServiceManager _manager;
    private readonly IServiceProcessHost _processHost;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<ManagedServiceViewModel> Services { get; } = [];

    public ServicesViewModel(IServiceManager manager, IServiceProcessHost processHost, ISettingsStore settings)
    {
        _manager = manager;
        _processHost = processHost;

        foreach (var service in manager.Services.OrderBy(s => s.Catalog.DisplayName, StringComparer.OrdinalIgnoreCase))
            Services.Add(new ManagedServiceViewModel(service, manager, processHost, settings));

        _processHost.StateChanged += OnServiceStateChanged;

        StatusMessage = $"Servers folder: {manager.ServersFolder}";
        _ = InitializeAsync();
    }

    /// <summary>First-load routine: read installed versions, launch auto-run services, then check GitHub.</summary>
    private async Task InitializeAsync()
    {
        await LoadInstalledAsync();
        await StartAutoRunServicesAsync();
        await CheckAllUpdatesAsync();
    }

    private async Task StartAutoRunServicesAsync()
    {
        foreach (var vm in Services)
        {
            try
            {
                await vm.StartForAutoRunAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Auto-run of {vm.DisplayName} failed: {ex.Message}";
            }
        }
    }

    private void OnServiceStateChanged(ManagedService service)
    {
        // Health/exit callbacks arrive on background threads — marshal to the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            var vm = Services.FirstOrDefault(v => v.Name == service.Catalog.Name);
            vm?.SyncFromModel();
        });
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
        catch (GithubAuthException)
        {
            StatusMessage = "GitHub rejected your token (401). Clear or replace it in Settings → GitHub token.";
        }
        finally
        {
            IsChecking = false;
        }
    }
}
