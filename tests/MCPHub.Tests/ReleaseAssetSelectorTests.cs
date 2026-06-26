using MCPHub.Core.Catalog;
using MCPHub.Core.Models;
using MCPHub.Core.Services.Github;
using Xunit;

namespace MCPHub.Tests;

public class ReleaseAssetSelectorTests
{
    private static ReleaseInfo Release(string version, params string[] assetNames) =>
        new(version, "v" + version, null, false,
            assetNames.Select(n => new ReleaseAsset(n, "https://example/" + n, 1)).ToList());

    [Fact]
    public void Selects_windows_self_contained()
    {
        var entry = ServiceCatalog.FindByName("NoteworthyMCPSharp")!;
        var release = Release("1.0.2",
            "NoteworthyMCPSharp-win-x64-self-contained-v1.0.2.zip",
            "NoteworthyMCPSharp-win-x64-framework-dependent-v1.0.2.zip",
            "NoteworthyMCPSharp-linux-x64-self-contained-v1.0.2.zip");

        var asset = ReleaseAssetSelector.Select(entry, release, PublishFlavor.SelfContained, isWindows: true);

        Assert.NotNull(asset);
        Assert.Equal("NoteworthyMCPSharp-win-x64-self-contained-v1.0.2.zip", asset!.Name);
    }

    [Fact]
    public void Selects_linux_framework_dependent()
    {
        var entry = ServiceCatalog.FindByName("SQLMCPSharp")!;
        var release = Release("1.0.2",
            "SQLMCPSharp-win-x64-self-contained-v1.0.2.zip",
            "SQLMCPSharp-linux-x64-framework-dependent-v1.0.2.zip");

        var asset = ReleaseAssetSelector.Select(entry, release, PublishFlavor.FrameworkDependent, isWindows: false);

        Assert.NotNull(asset);
        Assert.Equal("SQLMCPSharp-linux-x64-framework-dependent-v1.0.2.zip", asset!.Name);
    }

    [Fact]
    public void Returns_null_when_no_matching_asset()
    {
        var entry = ServiceCatalog.FindByName("GithubMCPSharp")!;
        var release = Release("1.0.2", "GithubMCPSharp-linux-x64-self-contained-v1.0.2.zip");

        var asset = ReleaseAssetSelector.Select(entry, release, PublishFlavor.SelfContained, isWindows: true);

        Assert.Null(asset);
    }
}
