using System.Runtime.InteropServices;
using MCPHub.Core.Catalog;
using MCPHub.Core.Models;

namespace MCPHub.Core.Services.Github;

/// <summary>Pure helpers for picking the correct release asset for an OS + publish flavour.</summary>
public static class ReleaseAssetSelector
{
    public static string OsToken(bool isWindows) => isWindows ? "win" : "linux";

    public static string FlavorToken(PublishFlavor flavor)
        => flavor == PublishFlavor.SelfContained ? "self-contained" : "framework-dependent";

    /// <summary>Finds the asset named <c>{Name}-{os}-x64-{flavor}-v{version}.zip</c> in the release.</summary>
    public static ReleaseAsset? Select(ServiceCatalogEntry entry, ReleaseInfo release, PublishFlavor flavor, bool isWindows)
    {
        var expected = entry.AssetFileName(OsToken(isWindows), FlavorToken(flavor), release.Version);
        return release.Assets.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Convenience overload using the current OS.</summary>
    public static ReleaseAsset? Select(ServiceCatalogEntry entry, ReleaseInfo release, PublishFlavor flavor)
        => Select(entry, release, flavor, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
}
