using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MCPHub.Core.Models;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Updates;

namespace MCPHub.App.ViewModels;

/// <summary>
/// Compares the running MCPHub build against the newest <c>Wixely/MCPHub</c> release and links out to
/// that release's GitHub page. Check-only by design: MCPHub never replaces its own executable — the
/// user downloads the zip and swaps it in themselves.
/// </summary>
public sealed partial class UpdatesViewModel : ViewModelBase
{
    private readonly IReleaseService _releases;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private UpdateStatus _status = UpdateStatus.Unknown;

    [ObservableProperty] private string _latestVersion = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPublished))]
    private string? _publishedText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadHint))]
    private string? _downloadHint;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isChecking;

    public UpdatesViewModel(IReleaseService releases)
    {
        _releases = releases;

        // Seed from the persisted release cache so the page opens populated with no network call —
        // a live check stays an explicit button press (GitHub allows only 60/hour unauthenticated).
        if (_releases.GetCachedRelease(HubRelease.Catalog) is { } cached)
        {
            Apply(cached);
            StatusMessage = "Showing the last release seen. Check for updates to refresh.";
        }
    }

    /// <summary>Version of the running build, e.g. <c>0.4.3</c>.</summary>
    public string CurrentVersion => HubRelease.CurrentVersion;

    public string RepositoryUrl => HubRelease.Catalog.RepositoryUrl;

    public string StatusText => Status switch
    {
        UpdateStatus.UpdateAvailable => "Update available",
        UpdateStatus.UpToDate => "Up to date",
        _ => "Not checked yet",
    };

    public bool HasPublished => !string.IsNullOrEmpty(PublishedText);

    public bool HasDownloadHint => !string.IsNullOrEmpty(DownloadHint);

    /// <summary>Asks GitHub for the newest release and recomputes the comparison.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsChecking)
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
                ? $"{release.Version} is available — open the release page to download it."
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
        LatestVersion = release.Version;
        Status = UpdateStatusCalculator.Compute(CurrentVersion, release.Version);
        PublishedText = release.PublishedAt?.ToLocalTime().ToString("d MMM yyyy");

        var asset = HubRelease.AssetForThisOs(release);
        DownloadHint = asset is null ? null : $"{asset.Name} ({FormatSize(asset.Size)})";
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{bytes / 1024d:0} KB";
}
