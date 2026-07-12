using MCPHub.Core.Catalog;

namespace MCPHub.Core.Slopworks;

/// <summary>
/// Static description of the Slopworks product — a Wixely desktop tool that installs and manages a
/// vLLM server on Windows via WSL2 + Podman. Not itself an MCP server: MCPHub can install/update
/// the binary and invoke its start/stop/status CLI commands. Release asset naming differs from the
/// MCPSharp default (<c>Slopworks-v{ver}-{os}-x64.zip</c>) so it uses the
/// <see cref="ServiceCatalogEntry.AssetFileNameOverride"/> hook.
/// </summary>
public static class Slopworks
{
    /// <summary>Log-store key (and Logs-page label) for CLI invocation output.</summary>
    public const string LogKey = "Slopworks";

    /// <summary>Reuses the catalog-entry machinery (release lookup, download/extract, manifest) for Slopworks.</summary>
    public static ServiceCatalogEntry Catalog { get; } = new(
        Name: "Slopworks",
        RepoOwner: "Wixely",
        RepoName: "Slopworks",
        DisplayName: "Slopworks",
        Description: "vLLM setup + management for Windows (WSL2 + Podman + OpenAI-compatible API)",
        DefaultPort: null,                    // Slopworks itself is not a listener; the vLLM container is.
        EnvPrefix: "SLOPWORKS_",
        ExecutableBaseName: "Slopworks.App",  // Publish output name (Slopworks.App.exe on Windows).
        ConfigFileNameOverride: "slopworks.json",  // Slopworks keeps its config under %AppData%; this is a placeholder for the record shape.
        AssetFileNameOverride: static (os, _flavor, tag) =>
        {
            // Slopworks ships one asset per OS, no flavour token in the name. Linux is .tar.gz;
            // MCPHub's DownloadService only extracts .zip today, so Windows-first is what's wired.
            var trimmed = tag.TrimStart('v');
            return os == "win"
                ? $"Slopworks-v{trimmed}-win-x64.zip"
                : $"Slopworks-v{trimmed}-linux-x64.tar.gz";
        });
}
