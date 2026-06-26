using System.Runtime.InteropServices;

namespace MCPHub.Core.Catalog;

/// <summary>
/// Static, compile-time description of one Wixely MCPSharp product. Everything here is
/// invariant for the product (repo coordinates, naming conventions); per-install runtime
/// state lives on <see cref="Models.ManagedService"/>.
/// </summary>
/// <param name="Name">
/// Canonical product name, e.g. <c>"NoteworthyMCPSharp"</c>. Doubles as the executable base name
/// and the config file base name (configs are uniquely named so all products share one folder).
/// </param>
/// <param name="RepoOwner">GitHub owner, always <c>"Wixely"</c>.</param>
/// <param name="RepoName">GitHub repository name (equal to <paramref name="Name"/> for this suite).</param>
/// <param name="DisplayName">Short human-friendly name for the UI, e.g. <c>"Noteworthy"</c>.</param>
/// <param name="Description">One-line description shown in the services list.</param>
/// <param name="DefaultPort">
/// Known default HTTP port the server listens on, or <see langword="null"/> when unknown — in which
/// case the effective port is read from the installed <c>{Name}.json</c> <c>Server</c> section.
/// </param>
/// <param name="EnvPrefix">
/// Per-service environment-variable override prefix, e.g. <c>"NOTEWORTHYMCP_"</c> (with <c>__</c> nesting).
/// </param>
public sealed record ServiceCatalogEntry(
    string Name,
    string RepoOwner,
    string RepoName,
    string DisplayName,
    string Description,
    int? DefaultPort,
    string EnvPrefix)
{
    /// <summary>Config file name read next to the executable, e.g. <c>NoteworthyMCPSharp.json</c>.</summary>
    public string ConfigFileName => $"{Name}.json";

    /// <summary>
    /// Short lowercase slug used to namespace this server's tools in the proxy (product name minus the
    /// trailing "MCPSharp"), e.g. <c>noteworthy</c> for <c>NoteworthyMCPSharp</c>.
    /// </summary>
    public string Key => Name.EndsWith("MCPSharp", StringComparison.OrdinalIgnoreCase)
        ? Name[..^"MCPSharp".Length].ToLowerInvariant()
        : Name.ToLowerInvariant();

    /// <summary>Executable file name for the given OS (<c>{Name}.exe</c> on Windows, <c>{Name}</c> elsewhere).</summary>
    public string ExecutableFileName(bool isWindows) => isWindows ? $"{Name}.exe" : Name;

    /// <summary>Executable file name for the current OS.</summary>
    public string ExecutableFileName() => ExecutableFileName(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    /// <summary>GitHub "latest release" API endpoint for this product.</summary>
    public string LatestReleaseApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>Web URL of the GitHub repository.</summary>
    public string RepositoryUrl => $"https://github.com/{RepoOwner}/{RepoName}";

    /// <summary>
    /// Release asset file name for a given OS token (<c>win</c>/<c>linux</c>), flavour token
    /// (<c>self-contained</c>/<c>framework-dependent</c>) and version tag, e.g.
    /// <c>NoteworthyMCPSharp-win-x64-self-contained-v1.0.2.zip</c>.
    /// </summary>
    public string AssetFileName(string osToken, string flavorToken, string versionTag)
        => $"{Name}-{osToken}-x64-{flavorToken}-v{versionTag.TrimStart('v')}.zip";
}
