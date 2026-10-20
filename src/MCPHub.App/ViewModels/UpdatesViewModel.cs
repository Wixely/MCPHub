using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MCPHub.App.Messages;
using MCPHub.Core.Models;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Updates;

namespace MCPHub.App.ViewModels;

/// <summary>
/// Compares the running MCPHub build against the newest <c>Wixely/MCPHub</c> release and, for a published
/// build, installs it: the matching zip is downloaded and verified, swapped in beside the running file, and
/// the new build started as a replacement that takes over when this process exits. A development build
/// only links out to the release page.
/// </summary>
public sealed partial class UpdatesViewModel : ViewModelBase
{
    private readonly IReleaseService _releases;
    private readonly IHubUpdater _updater;
    private readonly HubUpdateChecker _checker;
    private ReleaseInfo? _latest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    private UpdateStatus _status = UpdateStatus.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    private string _latestVersion = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPublished))]
    private string? _publishedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadHint))]
    private string? _downloadHint;

    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isInstalling;

    [ObservableProperty] private double _installProgress;
    [ObservableProperty] private bool _installProgressIsIndeterminate;
    [ObservableProperty] private string? _installPhase;
    [ObservableProperty] private string _lastCheckedText = "Never";
    [ObservableProperty] private string _autoCheckText = string.Empty;

    public UpdatesViewModel(IReleaseService releases, IHubUpdater updater, HubUpdateChecker checker)
    {
        _releases = releases;
        _updater = updater;
        _checker = checker;

        // Seed from the persisted release cache so the page opens populated with no network call. Live
        // checks are the button, or the automatic one when the last answer is older than two days.
        if (_releases.GetCachedRelease(HubRelease.Catalog) is { } cached)
        {
            Apply(cached);
            StatusMessage = "Showing the last release seen. Check for updates to refresh.";
        }

        RefreshCheckTimes();
        _checker.UpdateAvailable += release => Dispatcher.UIThread.Post(() =>
        {
            Apply(release);
            RefreshCheckTimes();
            StatusMessage = $"{release.Version} is available.";
        });
    }

    /// <summary>Version of the running build, e.g. <c>0.4.3</c>.</summary>
    public string CurrentVersion => HubRelease.CurrentVersion;

    /// <summary>What was built, which is what the update must match: the runtime identifier and whether it is a published single file.</summary>
    public string BuildText => _updater.Build.CanSelfUpdate
        ? $"{_updater.Build.RuntimeIdentifier}, published build"
        : $"{_updater.Build.RuntimeIdentifier}, not a published build";

    public string RepositoryUrl => HubRelease.Catalog.RepositoryUrl;

    public string StatusText => Status switch
    {
        UpdateStatus.UpdateAvailable => "Update available",
        UpdateStatus.UpToDate => "Up to date",
        _ => "Not checked yet",
    };

    public bool HasPublished => !string.IsNullOrEmpty(PublishedText);

    public bool HasDownloadHint => !string.IsNullOrEmpty(DownloadHint);

    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;

    public bool IsBusy => IsChecking || IsInstalling;

    /// <summary>Whether the Install button does anything: a newer release, with a build for this machine, on a build that can swap itself.</summary>
    public bool CanInstall => HasUpdate && !IsBusy && SelfUpdateBlocker is null;

    /// <summary>
    /// Why this MCPHub cannot install a release over itself, whether or not one is waiting, or
    /// <see langword="null"/> when it can. Shown all the time: a page that only explains itself once an
    /// update appears looks, the rest of the time, as though self-update does not exist.
    /// </summary>
    public string? SelfUpdateBlocker
    {
        get
        {
            if (_updater.Build.SelfUpdateBlocker is { } blocker)
                return blocker;
            if (_latest is not null && HubRelease.AssetFor(_latest, _updater.Build.RuntimeIdentifier) is null)
                return $"Release {_latest.Version} has no build for {_updater.Build.RuntimeIdentifier}, so there is nothing for this machine to install.";
            return null;
        }
    }

    /// <summary>The Self-update row: what Install will do here, or why it cannot.</summary>
    public string SelfUpdateText => SelfUpdateBlocker
        ?? $"Available. Install downloads the {_updater.Build.RuntimeIdentifier} build, verifies it, swaps it in and restarts.";

    public bool CanSelfUpdate => SelfUpdateBlocker is null;

    public string InstallButtonText => HasUpdate ? $"Install {LatestVersion} and restart" : "No update to install";

    /// <summary>Asks GitHub for the newest release and recomputes the comparison.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy)
            return;

        IsChecking = true;
        StatusMessage = "Checking GitHub for the latest MCPHub release…";
        try
        {
            var release = await _releases.GetLatestReleaseAsync(HubRelease.Catalog);
            if (release is null)
            {
                StatusMessage = "Couldn't reach GitHub, or no release is published yet. Try again shortly.";
                return;
            }

            Apply(release);
            StatusMessage = Status == UpdateStatus.UpdateAvailable
                ? (CanInstall
                    ? $"{release.Version} is available — install it from here, or download it from the release page."
                    : $"{release.Version} is available — open the release page to download it.")
                : $"{CurrentVersion} is the newest release.";
        }
        catch (GithubAuthException)
        {
            StatusMessage = "GitHub rejected your token (401). Clear or replace it in Settings.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Update check failed: " + ex.Message;
        }
        finally
        {
            IsChecking = false;
            RefreshCheckTimes();
        }
    }

    /// <summary>
    /// Downloads and verifies the newest release's build for this machine, swaps it in, and hands over to
    /// it. Each stage reports what it is doing; a failure at any stage leaves the running build untouched.
    /// </summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!CanInstall || _latest is null)
            return;

        IsInstalling = true;
        InstallProgress = 0;
        InstallProgressIsIndeterminate = false;
        var release = _latest;
        var progress = new Progress<HubUpdateProgress>(p =>
        {
            InstallPhase = p.Phase;
            InstallProgressIsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } fraction)
                InstallProgress = fraction;
        });

        StagedHubUpdate? staged = null;
        try
        {
            StatusMessage = $"Downloading MCPHub {release.Version}…";
            staged = await _updater.StageAsync(release, progress, CancellationToken.None);

            StatusMessage = staged.ChecksumVerified
                ? $"Verified {release.Version} against the release checksum. Installing…"
                : $"Downloaded {release.Version} (this release publishes no checksum). Installing…";
            InstallPhase = "Installing";
            InstallProgressIsIndeterminate = true;

            // Off the UI thread: the swap is file I/O and the launch waits on process creation.
            var failure = await Task.Run(() => _updater.Apply(staged));
            if (failure is not null)
            {
                StatusMessage = failure;
                return;
            }

            staged = null; // consumed: the updater is running from it
            StatusMessage = $"MCPHub {release.Version} is ready. Closing so it can install itself and start…";
            InstallPhase = "Restarting";
            WeakReferenceMessenger.Default.Send(new RestartForUpdateMessage(release.Version));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Update cancelled. Nothing has been changed.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Update failed: " + ex.Message + " Nothing has been changed.";
        }
        finally
        {
            if (staged is not null)
                _updater.Discard(staged);
            IsInstalling = false;
            InstallPhase = null;
        }
    }

    /// <summary>Opens the folder holding the running executable, where the downloaded build gets swapped in.</summary>
    [RelayCommand]
    private void OpenInstallFolder()
    {
        var folder = System.IO.Path.GetDirectoryName(_updater.Build.ExecutablePath ?? Environment.ProcessPath) ?? AppContext.BaseDirectory;
        try
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't open the install folder: " + ex.Message;
        }
    }

    /// <summary>Opens GitHub's newest-release page in the default browser, for a manual download.</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(HubRelease.LatestReleasePageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't open the browser: " + ex.Message;
        }
    }

    private void Apply(ReleaseInfo release)
    {
        _latest = release;
        LatestVersion = release.Version;
        Status = UpdateStatusCalculator.Compute(CurrentVersion, release.Version);
        PublishedText = release.PublishedAt?.ToLocalTime().ToString("d MMM yyyy");

        var asset = HubRelease.AssetFor(release, _updater.Build.RuntimeIdentifier);
        DownloadHint = asset is null ? null : $"{asset.Name} ({FormatSize(asset.Size)})";
        OnPropertyChanged(nameof(SelfUpdateBlocker));
        OnPropertyChanged(nameof(SelfUpdateText));
        OnPropertyChanged(nameof(CanSelfUpdate));
        OnPropertyChanged(nameof(CanInstall));
    }

    private void RefreshCheckTimes()
    {
        var last = _checker.LastCheckedAt;
        LastCheckedText = last is null ? "Never" : $"{Describe(DateTimeOffset.UtcNow - last.Value)} ago";

        if (!_checker.IsEnabled)
            AutoCheckText = "Automatic checks are off (Settings).";
        else if (_checker.NextCheckAt is { } next)
            AutoCheckText = $"Checked automatically every {HubUpdatePolicy.AutoCheckInterval.TotalHours:0} hours; next in {Describe(next - DateTimeOffset.UtcNow)}.";
        else
            AutoCheckText = $"Checked automatically every {HubUpdatePolicy.AutoCheckInterval.TotalHours:0} hours; a check is due.";
    }

    private static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalMinutes < 1)
            return "under a minute";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes} min";
        if (span.TotalDays < 1)
            return $"{(int)span.TotalHours} h";
        return $"{(int)span.TotalDays} d";
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{bytes / 1024d:0} KB";
}
