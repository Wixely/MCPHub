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

    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>
    /// Every managed server. Bulk operations (update checks, auto-run, state sync) always walk this
    /// list, never <see cref="FilteredServices"/> — filtering is a view concern and must not quietly
    /// narrow which services get started or checked.
    /// </summary>
    public ObservableCollection<ManagedServiceViewModel> Services { get; } = [];

    /// <summary>The subset shown in the list, narrowed by <see cref="FilterText"/>.</summary>
    public ObservableCollection<ManagedServiceViewModel> FilteredServices { get; } = [];

    public ServicesViewModel(IServiceManager manager, IServiceProcessHost processHost, ISettingsStore settings)
    {
        _manager = manager;
        _processHost = processHost;

        foreach (var service in manager.Services.OrderBy(s => s.Catalog.DisplayName, StringComparer.OrdinalIgnoreCase))
            Services.Add(new ManagedServiceViewModel(service, manager, processHost, settings));

        ApplyFilter();

        _processHost.StateChanged += OnServiceStateChanged;
        // Installs and update checks can also be driven by an agent (mcphub__install / mcphub__update /
        // mcphub__check_service_updates); refresh the row when the manager reports a change from anywhere.
        _manager.ServiceChanged += OnServiceStateChanged;

        StatusMessage = $"Servers folder: {manager.ServersFolder}";
        _ = InitializeAsync();
    }

    /// <summary>True when a filter is active but nothing matches, so the list can explain itself.</summary>
    public bool HasNoMatches => FilteredServices.Count == 0 && Services.Count > 0;

    /// <summary>e.g. "3 of 20 servers" while filtering; empty when showing everything.</summary>
    public string FilterSummary => FilterText.Trim().Length == 0
        ? string.Empty
        : $"{FilteredServices.Count} of {Services.Count} servers";

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Rebuild <see cref="FilteredServices"/> from <see cref="Services"/>. The match itself lives on
    /// ServiceCatalogEntry, where it is unit-testable without pulling Avalonia into the test project.
    /// </summary>
    private void ApplyFilter()
    {
        FilteredServices.Clear();
        foreach (var vm in Services)
        {
            if (vm.MatchesSearch(FilterText))
                FilteredServices.Add(vm);
        }

        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(FilterSummary));
    }

    [RelayCommand]
    private void ClearFilter() => FilterText = string.Empty;

    /// <summary>
    /// First-load routine: read installed versions and launch auto-run services. Latest versions come from
    /// the persisted release cache with no network call; GitHub is queried automatically only the first time
    /// (no cache yet) — afterwards the user refreshes with "Check for updates".
    /// </summary>
    private async Task InitializeAsync()
    {
        await LoadInstalledAsync();
        await StartAutoRunServicesAsync();

        var cacheHits = 0;
        foreach (var vm in Services)
        {
            if (vm.ApplyCachedLatest())
                cacheHits++;
        }

        if (cacheHits > 0)
            StatusMessage = "Showing last-known versions — click 'Check for updates' to refresh.";
        else
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
