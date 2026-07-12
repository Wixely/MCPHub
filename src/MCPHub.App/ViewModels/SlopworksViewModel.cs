using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Models;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Slopworks;

namespace MCPHub.App.ViewModels;

/// <summary>
/// Drives the Slopworks tab: install/update from GitHub releases, plus the vLLM-server start/stop
/// buttons that shell out to <c>Slopworks.App.exe start</c> / <c>stop</c>, plus a Refresh button
/// that re-runs <c>Slopworks.App.exe status --json</c>. Status is NOT polled — refreshes on tab
/// activation and manual refresh only (per product decision: the CLI already schedules its own
/// container health probes; MCPHub only samples).
/// </summary>
public sealed partial class SlopworksViewModel : ViewModelBase
{
    private readonly ISlopworksService _service;
    private readonly ISlopworksCli _cli;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string? _statusMessage;

    // Cached status snapshot from the last CLI invocation.
    [ObservableProperty]
    private string _containerState = "—";

    [ObservableProperty]
    private bool _apiHealthy;

    [ObservableProperty]
    private string _endpoint = "—";

    [ObservableProperty]
    private string _model = "—";

    [ObservableProperty]
    private int _port;

    public SlopworksViewModel(ISlopworksService service, ISlopworksCli cli)
    {
        _service = service;
        _cli = cli;
        _ = InitializeAsync();
    }

    private ManagedService Model_ => _service.Slopworks;

    public string DisplayName => Model_.Catalog.DisplayName;
    public string Description => Model_.Catalog.Description;
    public string InstalledVersion => Model_.InstalledVersion ?? "—";
    public string LatestVersion => Model_.LatestVersion ?? "—";
    public UpdateStatus UpdateStatus => Model_.UpdateStatus;
    public bool IsInstalled => Model_.IsInstalled;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public string ApiHealthText => ApiHealthy ? "healthy" : "not responding";
    public string PortText => Port > 0 ? Port.ToString() : "—";

    public string InstallButtonText => !Model_.IsInstalled
        ? "Install"
        : Model_.UpdateStatus == UpdateStatus.UpdateAvailable ? "Update" : "Reinstall";

    private async Task InitializeAsync()
    {
        try
        {
            await _service.RefreshInstalledAsync();
            await CheckUpdatesAsync();
            if (Model_.IsInstalled)
                await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Slopworks init failed: " + ex.Message;
        }
        finally
        {
            SyncFromModel();
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Checking GitHub for the latest Slopworks release…";
        try
        {
            var release = await _service.CheckForUpdatesAsync();
            StatusMessage = release is null
                ? "Couldn't reach GitHub for the latest release."
                : $"Latest Slopworks release: {release.Version}.";
        }
        catch (GithubAuthException)
        {
            StatusMessage = "GitHub rejected your token (401). Clear or replace it in Settings → GitHub token.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Update check failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            SyncFromModel();
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsInstalling) return;
        IsInstalling = true;
        InstallProgress = 0;
        var progress = new Progress<double>(p => InstallProgress = p);
        try
        {
            await _service.InstallOrUpdateAsync(progress);
            StatusMessage = $"Installed Slopworks {Model_.InstalledVersion}.";
        }
        catch (GithubAuthException)
        {
            StatusMessage = "GitHub rejected your token (401). Clear or replace it in Settings → GitHub token.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Install failed: " + ex.Message;
        }
        finally
        {
            IsInstalling = false;
            SyncFromModel();
            if (Model_.IsInstalled)
                await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        if (!Model_.IsInstalled)
        {
            ApplyStatus(SlopworksStatus.Unknown);
            return;
        }

        StatusMessage = "Querying Slopworks status…";
        try
        {
            var status = await _cli.GetStatusAsync();
            ApplyStatus(status);
            StatusMessage = status.ApiHealthy
                ? $"vLLM API healthy at {status.Endpoint}."
                : $"Container: {status.ContainerState}. API not responding.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Status query failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!Model_.IsInstalled) return;
        StatusMessage = "Starting vLLM (first pull can take a while — see the Logs tab)…";
        try
        {
            var exit = await _cli.StartAsync();
            StatusMessage = exit == 0 ? "Start succeeded." : $"Start exited with code {exit}. See the Logs tab.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Start failed: " + ex.Message;
        }
        finally
        {
            await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!Model_.IsInstalled) return;
        StatusMessage = "Stopping vLLM…";
        try
        {
            var exit = await _cli.StopAsync();
            StatusMessage = exit == 0 ? "Stopped." : $"Stop exited with code {exit}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Stop failed: " + ex.Message;
        }
        finally
        {
            await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Model_.InstallFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open slopworks folder '{folder}': {ex.Message}");
        }
    }

    private void ApplyStatus(SlopworksStatus s)
    {
        ContainerState = string.IsNullOrEmpty(s.ContainerState) ? "—" : s.ContainerState;
        ApiHealthy = s.ApiHealthy;
        Endpoint = string.IsNullOrEmpty(s.Endpoint) ? "—" : s.Endpoint;
        Model = string.IsNullOrEmpty(s.Model) ? "—" : s.Model;
        Port = s.Port;
        OnPropertyChanged(nameof(ApiHealthText));
        OnPropertyChanged(nameof(PortText));
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnApiHealthyChanged(bool value) => OnPropertyChanged(nameof(ApiHealthText));
    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(PortText));

    private void SyncFromModel()
    {
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(InstallButtonText));
    }
}
