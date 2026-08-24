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
/// Last-resort port used only when the installed config cannot be read at all. <see langword="null"/>
/// for every MCPSharp product — their port is read from the installed <c>{Name}.json</c>
/// <c>Server</c> section, which is the only copy this repo does not have to keep in sync.
/// Non-catalog entries that own their own port (e.g. <c>DaggerAgent</c>) still set it.
/// </param>
/// <param name="EnvPrefix">
/// Per-service environment-variable override prefix, e.g. <c>"NOTEWORTHYMCP_"</c> (with <c>__</c> nesting).
/// </param>
/// <param name="ExecutableBaseName">
/// Executable base name when it differs from <paramref name="Name"/> (e.g. <c>"dagger"</c> for the
/// <c>DaggerAgent</c> product); <see langword="null"/> falls back to <paramref name="Name"/>.
/// </param>
/// <param name="ConfigFileNameOverride">
/// Config file name when it isn't <c>{Name}.json</c> (e.g. <c>"appsettings.json"</c>); <see langword="null"/>
/// falls back to <c>{Name}.json</c>.
/// </param>
/// <param name="AssetFileNameOverride">
/// Callback for products whose GitHub release asset names don't follow the MCPSharp default
/// (<c>{Name}-{os}-x64-{flavor}-v{ver}.zip</c>) — e.g. <c>Slopworks</c> uses
/// <c>Slopworks-v{ver}-{os}-x64.zip</c> and drops the flavour token entirely. Args are
/// <c>(osToken, flavorToken, versionTag)</c>; <see langword="null"/> falls back to the default.
/// </param>
public sealed record ServiceCatalogEntry(
    string Name,
    string RepoOwner,
    string RepoName,
    string DisplayName,
    string Description,
    int? DefaultPort,
    string EnvPrefix,
    string? ExecutableBaseName = null,
    string? ConfigFileNameOverride = null,
    Func<string, string, string, string>? AssetFileNameOverride = null)
{
    /// <summary>
    /// Config file name read next to the executable, e.g. <c>NoteworthyMCPSharp.json</c> — or
    /// <see cref="ConfigFileNameOverride"/> when the product uses a non-standard name (e.g. <c>appsettings.json</c>).
    /// </summary>
    public string ConfigFileName => ConfigFileNameOverride ?? $"{Name}.json";

    /// <summary>
    /// Additional config files this product reads beside <see cref="ConfigFileName"/>, in the order
    /// they should appear in the UI. Empty for most products.
    ///
    /// These typically ship only as <c>.example.json</c> templates so an update cannot overwrite
    /// real data — see <see cref="ConfigFileResolver"/>, which promotes the template on first open.
    /// </summary>
    public IReadOnlyList<string> ExtraConfigFileNames { get; init; } = [];

    /// <summary>Primary config plus any extras, in display order.</summary>
    public IReadOnlyList<string> AllConfigFileNames =>
        [ConfigFileName, .. ExtraConfigFileNames];

    /// <summary>True when this product reads more than one config file.</summary>
    public bool HasExtraConfigFiles => ExtraConfigFileNames.Count > 0;

    /// <summary>
    /// Short lowercase slug used to namespace this server's tools in the proxy (product name minus the
    /// trailing "MCPSharp"), e.g. <c>noteworthy</c> for <c>NoteworthyMCPSharp</c>.
    /// </summary>
    public string Key => Name.EndsWith("MCPSharp", StringComparison.OrdinalIgnoreCase)
        ? Name[..^"MCPSharp".Length].ToLowerInvariant()
        : Name.ToLowerInvariant();

    /// <summary>
    /// Executable file name for the given OS (<c>{base}.exe</c> on Windows, <c>{base}</c> elsewhere),
    /// where <c>base</c> is <see cref="ExecutableBaseName"/> when set, otherwise <see cref="Name"/>.
    /// </summary>
    public string ExecutableFileName(bool isWindows)
    {
        var baseName = ExecutableBaseName ?? Name;
        return isWindows ? $"{baseName}.exe" : baseName;
    }

    /// <summary>Executable file name for the current OS.</summary>
    public string ExecutableFileName() => ExecutableFileName(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    /// <summary>
    /// Whether this product matches a free-text search term, for narrowing the services list.
    ///
    /// Both names are matched because they routinely differ: the list shows
    /// <see cref="DisplayName"/> ("Mail &amp; Calendar"), but someone hunting for it is just as
    /// likely to type the product name ("mailcal"). A blank term matches everything.
    /// </summary>
    public bool MatchesSearch(string? term)
    {
        var trimmed = term?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return true;

        return DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
               || Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

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
        => AssetFileNameOverride?.Invoke(osToken, flavorToken, versionTag)
           ?? $"{Name}-{osToken}-x64-{flavorToken}-v{versionTag.TrimStart('v')}.zip";
}
