using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Agent;
using MCPHub.Core.Models;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;

namespace MCPHub.App.ViewModels;

/// <summary>Drives the DaggerAgent tab: install/update, the run-mode launches, and per-mode auto-start.</summary>
public sealed partial class AgentViewModel : ViewModelBase
{
    private readonly IAgentService _agent;
    private readonly IAgentProcessHost _host;
    private readonly ISettingsStore _settings;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _installProgress;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _autoStartCli;

    [ObservableProperty]
    private bool _autoStartWeb;

    [ObservableProperty]
    private bool _autoStartJobs;

    [ObservableProperty]
    private bool _bindAllInterfaces;

    public AgentViewModel(IAgentService agent, IAgentProcessHost host, ISettingsStore settings)
    {
        _agent = agent;
        _host = host;
        _settings = settings;

        var s = settings.Current;
        _autoStartCli = s.AutoStartAgentCli;
        _autoStartWeb = s.AutoStartAgentWeb;
        _autoStartJobs = s.AutoStartAgentJobs;
        _bindAllInterfaces = s.AgentServeBindAllInterfaces;

        _host.StateChanged += OnHostStateChanged;
        _ = InitializeAsync();
    }

    private ManagedService Model => _agent.Agent;

    public string DisplayName => Model.Catalog.DisplayName;
    public string Description => Model.Catalog.Description;
    public string InstalledVersion => Model.InstalledVersion ?? "—";
    public string LatestVersion => Model.LatestVersion ?? "—";
    public UpdateStatus UpdateStatus => Model.UpdateStatus;
    public ServiceRunState RunState => Model.RunState;
    public string RunStateText => Model.RunState.ToString();
    public string ServingModeText => _host.ServingMode is { } m ? m.ToString() : "—";
    public bool IsInstalled => Model.IsInstalled;
    public bool CanStop => _host.IsServing;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public string InstallButtonText => !Model.IsInstalled
        ? "Install"
        : Model.UpdateStatus == UpdateStatus.UpdateAvailable ? "Update" : "Reinstall";

    public string ConfigFileName => Model.Catalog.ConfigFileName;
    public bool CanEditConfig => File.Exists(Model.ConfigPath);

    private void OnHostStateChanged() => Dispatcher.UIThread.Post(SyncFromModel);

    private async Task InitializeAsync()
    {
        try
        {
            await _agent.RefreshInstalledAsync();
            SyncFromModel();
            await AutoStartAsync();
            await CheckUpdatesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Agent init failed: " + ex.Message;
        }
    }

    /// <summary>Starts the auto-start mode(s) on launch (Web/Jobs are mutually exclusive; CLI is independent).</summary>
    private async Task AutoStartAsync()
    {
        if (!Model.IsInstalled)
            return;

        var s = _settings.Current;
        if (s.AutoStartAgentJobs)
            await _host.StartServeAsync(AgentRunMode.Jobs, bindAllInterfaces: s.AgentServeBindAllInterfaces);
        else if (s.AutoStartAgentWeb)
            await _host.StartServeAsync(AgentRunMode.Web, bindAllInterfaces: s.AgentServeBindAllInterfaces);

        if (s.AutoStartAgentCli)
            _host.LaunchCli();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusMessage = "Checking GitHub for the latest DaggerAgent release…";
        try
        {
            var release = await _agent.CheckForUpdatesAsync();
            StatusMessage = release is null
                ? "Couldn't reach GitHub for the latest release."
                : $"Latest DaggerAgent release: {release.Version}.";
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
        if (IsInstalling)
            return;

        IsInstalling = true;
        InstallProgress = 0;
        var progress = new Progress<double>(p => InstallProgress = p);
        try
        {
            await _agent.InstallOrUpdateAsync(progress);
            StatusMessage = $"Installed DaggerAgent {Model.InstalledVersion}.";
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
        }
    }

    [RelayCommand]
    private void StartCli() => _host.LaunchCli();

    [RelayCommand]
    private async Task StartWebAsync()
    {
        StatusMessage = "Starting agent (web) — the UI opens once it's ready…";
        await _host.StartServeAsync(AgentRunMode.Web, openBrowserWhenReady: true, bindAllInterfaces: BindAllInterfaces);
    }

    [RelayCommand]
    private async Task StartJobsAsync()
    {
        StatusMessage = "Starting agent (jobs) — the UI opens once it's ready…";
        await _host.StartServeAsync(AgentRunMode.Jobs, openBrowserWhenReady: true, bindAllInterfaces: BindAllInterfaces);
    }

    [RelayCommand]
    private Task StopAsync() => _host.StopAsync();

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Model.InstallFolder;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open agent folder '{folder}': {ex.Message}");
        }
    }

    [RelayCommand]
    private void EditConfig()
    {
        var path = Model.ConfigPath;
        if (!File.Exists(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open agent config '{path}': {ex.Message}");
        }
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnAutoStartCliChanged(bool value)
    {
        _settings.Current.AutoStartAgentCli = value;
        _ = _settings.SaveAsync();
    }

    partial void OnAutoStartWebChanged(bool value)
    {
        _settings.Current.AutoStartAgentWeb = value;
        if (value)
            AutoStartJobs = false; // Web and Jobs share the serve port
        _ = _settings.SaveAsync();
    }

    partial void OnAutoStartJobsChanged(bool value)
    {
        _settings.Current.AutoStartAgentJobs = value;
        if (value)
            AutoStartWeb = false;
        _ = _settings.SaveAsync();
    }

    partial void OnBindAllInterfacesChanged(bool value)
    {
        _settings.Current.AgentServeBindAllInterfaces = value;
        _ = _settings.SaveAsync();
    }

    public void SyncFromModel()
    {
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(RunState));
        OnPropertyChanged(nameof(RunStateText));
        OnPropertyChanged(nameof(ServingModeText));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(CanEditConfig));
    }
}
