using MCPHub.App.ViewModels;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;
using MCPHub.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static MCPHub.Tests.ManagementTestKit;

namespace MCPHub.Tests;

/// <summary>
/// What the Updates page shows about self-update. The page once hid both the Install button and the reason
/// it could not install unless a newer release existed, so a developer build already on the newest version
/// showed nothing at all and looked as though the feature was missing. Both are now always on the page.
/// </summary>
public class UpdatesViewModelTests
{
    private static readonly HubBuild Published = new("win-x64", "C:/sbin/MCPHub/MCPHub.exe", IsSingleFilePublish: true, IsLaunchedByAHost: false);
    private static readonly HubBuild VsCodeLaunch = HubBuild.Describe("win-x64", "C:/Program Files/dotnet/dotnet.exe", "C:/src/bin/MCPHub.dll", "MCPHub");

    private static UpdatesViewModel Page(HubBuild build, string? latest, params string[] assets)
    {
        var releases = new FakeReleaseService();
        var cached = latest is null ? null : Release(latest, assets);
        var settings = new FakeSettings();
        var checker = new HubUpdateChecker(new CachedReleases(releases, cached), settings, NullLogger<HubUpdateChecker>.Instance);
        return new UpdatesViewModel(new CachedReleases(releases, cached), new FakeUpdater(build), checker);
    }

    [Fact]
    public void A_vscode_launch_on_the_newest_release_still_says_why_it_cannot_update_itself()
    {
        var page = Page(VsCodeLaunch, HubRelease.CurrentVersion, "MCPHub-win-x64-v" + HubRelease.CurrentVersion + ".zip");

        Assert.False(page.HasUpdate);
        Assert.False(page.CanSelfUpdate);
        Assert.False(page.CanInstall);
        Assert.Contains("dotnet", page.SelfUpdateText);
        Assert.Equal("No update to install", page.InstallButtonText);
    }

    [Fact]
    public void A_published_build_says_self_update_is_available_even_when_up_to_date()
    {
        var page = Page(Published, latest: null);

        Assert.True(page.CanSelfUpdate);
        Assert.StartsWith("Available", page.SelfUpdateText);
        Assert.False(page.CanInstall); // nothing newer to install
    }

    [Fact]
    public void A_published_build_with_a_newer_release_can_install_it()
    {
        var page = Page(Published, "99.0.0", "MCPHub-win-x64-v99.0.0.zip", "MCPHub-linux-x64-v99.0.0.zip");

        Assert.True(page.HasUpdate);
        Assert.True(page.CanInstall);
        Assert.Equal("Install 99.0.0 and restart", page.InstallButtonText);
    }

    [Fact]
    public void A_newer_release_with_no_build_for_this_machine_says_so()
    {
        var page = Page(Published, "99.0.0", "MCPHub-linux-x64-v99.0.0.zip");

        Assert.True(page.HasUpdate);
        Assert.False(page.CanInstall);
        Assert.Contains("no build for win-x64", page.SelfUpdateText);
    }

    /// <summary>Serves one release from the "persisted cache", which is what the page reads on open.</summary>
    private sealed class CachedReleases(FakeReleaseService inner, ReleaseInfo? cached) : IReleaseService
    {
        public Task<ReleaseInfo?> GetLatestReleaseAsync(MCPHub.Core.Catalog.ServiceCatalogEntry entry, CancellationToken cancellationToken = default)
            => inner.GetLatestReleaseAsync(entry, cancellationToken);

        public ReleaseInfo? GetCachedRelease(MCPHub.Core.Catalog.ServiceCatalogEntry entry) => cached;

        public DateTimeOffset? GetLastCheckedAt(MCPHub.Core.Catalog.ServiceCatalogEntry entry) => null;
    }

    private sealed class FakeUpdater(HubBuild build) : IHubUpdater
    {
        public HubBuild Build => build;

        public Task<StagedHubUpdate> StageAsync(ReleaseInfo release, IProgress<HubUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public string? Apply(StagedHubUpdate staged) => throw new NotSupportedException();

        public void Discard(StagedHubUpdate staged) { }
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public MCPHubSettings Current { get; } = new();
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
