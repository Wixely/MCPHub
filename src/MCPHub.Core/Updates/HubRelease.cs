using System.Reflection;
using System.Runtime.InteropServices;
using MCPHub.Core.Catalog;
using MCPHub.Core.Services.Github;

namespace MCPHub.Core.Updates;

/// <summary>
/// Static description of MCPHub itself, for the Updates page. Deliberately <em>not</em> a member of
/// <see cref="ServiceCatalog.All"/> — MCPHub is not a managed server, and everything that iterates the
/// catalog (auto-start, proxy aggregation, the installed-versions manifest) must never treat it as one.
/// The entry exists only to reuse <see cref="IReleaseService"/>'s release lookup (ETag caching, PAT
/// auth, rate-limit handling) against <c>Wixely/MCPHub</c>.
/// </summary>
/// <remarks>
/// MCPHub ships as a single-file self-contained executable, one asset per OS
/// (<c>MCPHub-win-x64-v{ver}.zip</c>) with no flavour token — so the MCPSharp asset-naming default
/// does not apply and no download path is wired up. Updates are grabbed manually from the release page.
/// </remarks>
public static class HubRelease
{
    /// <summary>Reuses the catalog-entry machinery (repo coordinates, release URLs) for MCPHub itself.</summary>
    public static ServiceCatalogEntry Catalog { get; } = new(
        Name: "MCPHub",
        RepoOwner: "Wixely",
        RepoName: "MCPHub",
        DisplayName: "MCPHub",
        Description: "The hub app itself",
        DefaultPort: null,     // The aggregating proxy's port is a user setting, not a product default.
        EnvPrefix: "MCPHUB_");

    /// <summary>
    /// Version of the running build, e.g. <c>0.4.3</c> — read from the assembly stamped by
    /// <c>Directory.Build.props</c> (which versions every project alike). Reads <em>this</em> assembly
    /// rather than the entry assembly so it stays correct under a test host too.
    /// </summary>
    public static string CurrentVersion { get; } = ResolveCurrentVersion();

    /// <summary>
    /// GitHub's "newest release" page — where the user downloads a build. Always the rolling
    /// <c>/releases/latest</c> URL rather than a pinned tag, so it stays correct whether or not a check
    /// has run and however stale the cached release is.
    /// </summary>
    public static string LatestReleasePageUrl => $"{Catalog.RepositoryUrl}/releases/latest";

    /// <summary>
    /// The release asset for the given OS (<c>MCPHub-win-x64-…</c> / <c>MCPHub-linux-x64-…</c>), so the
    /// page can name the file to grab. <see langword="null"/> when the release has no asset for it.
    /// Matched on the OS token rather than the full name, since MCPHub's assets carry no flavour token.
    /// </summary>
    public static ReleaseAsset? AssetFor(ReleaseInfo release, bool isWindows)
    {
        var token = $"-{ReleaseAssetSelector.OsToken(isWindows)}-x64-";
        return release.Assets.FirstOrDefault(a => a.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Convenience overload using the current OS.</summary>
    public static ReleaseAsset? AssetForThisOs(ReleaseInfo release)
        => AssetFor(release, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    private static string ResolveCurrentVersion()
    {
        var assembly = typeof(HubRelease).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends "+{commit sha}" build metadata when source-link is on; SemVer-compare without it.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        // AssemblyVersion is four-part (0.4.3.0) and not valid SemVer — take the first three components.
        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
