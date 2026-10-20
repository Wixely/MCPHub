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
    [InlineData("win-x64", "MCPHub-win-x64-v0.4.3.zip")]
    [InlineData("linux-x64", "MCPHub-linux-x64-v0.4.3.zip")]
    public void Asset_for_a_runtime_matches_the_flavourless_hub_naming(string rid, string expected)
    {
        var release = Release("0.4.3", "MCPHub-win-x64-v0.4.3.zip", "MCPHub-linux-x64-v0.4.3.zip", "SHA256SUMS");

        Assert.Equal(expected, HubRelease.AssetFor(release, rid)?.Name);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("osx-arm64")]
    public void Asset_is_null_when_the_release_has_none_for_that_runtime(string rid)
    {
        // Including the arm64 cases: an arm64 machine must get "nothing to install", never the x64 file.
        var release = Release("0.4.3", "MCPHub-linux-x64-v0.4.3.zip", "SHA256SUMS");

        Assert.Null(HubRelease.AssetFor(release, rid));
    }

    [Fact]
    public void Checksum_file_is_found_by_name_and_only_by_name()
    {
        var with = Release("0.4.3", "MCPHub-win-x64-v0.4.3.zip", "sha256sums");
        var without = Release("0.4.3", "MCPHub-win-x64-v0.4.3.zip");

        Assert.Equal("sha256sums", HubRelease.ChecksumAssetFor(with)?.Name);
        Assert.Null(HubRelease.ChecksumAssetFor(without));
        // The checksum file is never mistaken for a build, whatever runtime is asked for.
        Assert.Null(HubRelease.AssetFor(Release("0.4.3", "SHA256SUMS"), "win-x64"));
    }
}
