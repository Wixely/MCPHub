using System.Text.RegularExpressions;
using MCPHub.Core.Catalog;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Updates;
using Xunit;

namespace MCPHub.Tests;

public class HubReleaseTests
{
    private static ReleaseInfo Release(string version, params string[] assetNames) =>
        new(version, "v" + version, null, false,
            assetNames.Select(n => new ReleaseAsset(n, "https://example/" + n, 1)).ToList());

    [Fact]
    public void Hub_is_not_a_managed_service()
    {
        // The Updates page checks MCPHub's own version, but MCPHub must never appear in the catalog:
        // everything iterating it (auto-start, proxy aggregation, the manifest) would treat it as a
        // spawnable server — a second instance of the app fighting for the proxy port.
        Assert.Null(ServiceCatalog.FindByName("MCPHub"));
        Assert.DoesNotContain(ServiceCatalog.All, e => e.RepoName == "MCPHub");
    }

    [Fact]
    public void Current_version_is_a_semver_triple_from_the_build()
    {
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+"), HubRelease.CurrentVersion);
        Assert.NotEqual("0.0.0", HubRelease.CurrentVersion);
    }

    [Fact]
    public void Release_page_url_is_githubs_rolling_latest_page()
    {
        // Rolling, not a pinned tag: correct before any check has run, and never points at a stale
        // cached release.
        Assert.Equal("https://github.com/Wixely/MCPHub/releases/latest", HubRelease.LatestReleasePageUrl);
    }

    [Theory]
    [InlineData(true, "MCPHub-win-x64-v0.4.3.zip")]
    [InlineData(false, "MCPHub-linux-x64-v0.4.3.zip")]
    public void Asset_for_os_matches_the_flavourless_hub_naming(bool isWindows, string expected)
    {
        var release = Release("0.4.3", "MCPHub-win-x64-v0.4.3.zip", "MCPHub-linux-x64-v0.4.3.zip");

        Assert.Equal(expected, HubRelease.AssetFor(release, isWindows)?.Name);
    }

    [Fact]
    public void Asset_for_os_is_null_when_the_release_has_none_for_it()
    {
        var release = Release("0.4.3", "MCPHub-linux-x64-v0.4.3.zip");

        Assert.Null(HubRelease.AssetFor(release, isWindows: true));
    }
}
