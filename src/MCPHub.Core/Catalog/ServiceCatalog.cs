namespace MCPHub.Core.Catalog;

/// <summary>
/// The fixed set of 17 Wixely MCPSharp products MCPHub manages.
/// </summary>
/// <remarks>
/// Most products live in a repo named after the product. The exception is <c>RepoDetoxMCPSharp</c>,
/// whose HTTP MCP server ships inside the multi-app <c>RepoDetox</c> repo (alongside a CLI and GUI),
/// so its <c>RepoName</c> differs from its <c>Name</c> and it is published self-contained only.
///
/// <para>
/// Every product's <c>DefaultPort</c> is <see langword="null"/> on purpose: the effective port is read
/// from that server's own installed <c>{Name}.json</c> by <c>ServerConfigReader</c>, which finds the
/// <c>Server.Port</c> at any nesting depth and so resolves all of them. Hard-coding a port here only
/// duplicates a value this repo does not own, and duplicated values drift — MailCal moved to 5717 and
/// Noteworthy has always shipped 5711, and both sat wrong in this list for as long as they were here.
/// </para>
///
/// Env-var prefixes follow the observed pattern (product name minus the trailing "Sharp",
/// upper-cased, plus "_") and are verified against the installed config later.
/// </remarks>
public static class ServiceCatalog
{
    /// <summary>All catalog entries, in display order.</summary>
    public static IReadOnlyList<ServiceCatalogEntry> All { get; } =
    [
        new("NoteworthyMCPSharp", "Wixely", "NoteworthyMCPSharp",
            "Noteworthy", "MIDI / notes library MCP server", null, "NOTEWORTHYMCP_"),

        new("SQLMCPSharp", "Wixely", "SQLMCPSharp",
            "SQL", "SQL databases (MSSQL, MySQL, …) MCP server", null, "SQLMCP_"),

        new("RedisMCPSharp", "Wixely", "RedisMCPSharp",
            "Redis", "Redis data store (read/write, search, diagnostics) MCP server", null, "REDISMCP_"),

        new("GithubMCPSharp", "Wixely", "GithubMCPSharp",
            "GitHub", "GitHub repositories & issues MCP server", null, "GITHUBMCP_"),

        new("GitlabMCPSharp", "Wixely", "GitlabMCPSharp",
            "GitLab", "GitLab projects & merge requests MCP server", null, "GITLABMCP_"),

        new("AzureDevopsMCPSharp", "Wixely", "AzureDevopsMCPSharp",
            "Azure DevOps", "Azure DevOps boards & repos MCP server", null, "AZUREDEVOPSMCP_"),

        new("HomeAssistantMCPSharp", "Wixely", "HomeAssistantMCPSharp",
            "Home Assistant", "Home Assistant smart-home MCP server", null, "HOMEASSISTANTMCP_"),

        new("PaperlessNgxMCPSharp", "Wixely", "PaperlessNgxMCPSharp",
            "Paperless-ngx", "Paperless-ngx document store MCP server", null, "PAPERLESSNGXMCP_"),

        new("MailCalMCPSharp", "Wixely", "MailCalMCPSharp",
            "Mail & Calendar", "Email & calendar (Outlook, Gmail, IMAP) MCP server", null, "MAILCALMCP_"),

        new("ProxmoxMCPSharp", "Wixely", "ProxmoxMCPSharp",
            "Proxmox", "Proxmox VE virtualization MCP server", null, "PROXMOXMCP_"),

        new("PortainerMCPSharp", "Wixely", "PortainerMCPSharp",
            "Portainer", "Portainer stacks & Docker containers MCP server", null, "PORTAINERMCP_"),

        new("RouterOSMCPSharp", "Wixely", "RouterOSMCPSharp",
            "RouterOS", "MikroTik RouterOS MCP server", null, "ROUTEROSMCP_"),

        // Reads two host inventories beside its own config. The release ships only the
        // *.example.json templates so an update can't overwrite real inventory; MCPHub promotes
        // a template the first time you open it.
        new("RemoteAdminMCPSharp", "Wixely", "RemoteAdminMCPSharp",
            "Remote Admin", "Remote administration (Windows WinRM / Linux SSH) MCP server", null, "REMOTEADMINMCP_")
        {
            ExtraConfigFileNames =
            [
                "remote_admin_windows_servers.json",
                "remote_admin_linux_servers.json",
            ],
        },

        new("ChromeDevToolsMCPSharp", "Wixely", "ChromeDevToolsMCPSharp",
            "Chrome DevTools", "Chrome DevTools Protocol MCP server", null, "CHROMEDEVTOOLSMCP_"),

        new("PlaywrightMCPSharp", "Wixely", "PlaywrightMCPSharp",
            "Playwright", "Playwright browser automation MCP server", null, "PLAYWRIGHTMCP_"),

        new("ComfyUIMCPSharp", "Wixely", "ComfyUIMCPSharp",
            "ComfyUI", "ComfyUI image generation (queue, live progress, images) MCP server", null, "COMFYUIMCP_"),

        // Non-standard: RepoDetoxMCPSharp is one of three apps (CLI, GUI, MCP) shipped from the
        // "RepoDetox" repo, so RepoName ≠ Name. Published self-contained only; port read from config.
        new("RepoDetoxMCPSharp", "Wixely", "RepoDetox",
            "Repo Detox", "Git history cleaner / anonymiser MCP server", null, "REPODETOXMCP_"),
    ];

    /// <summary>Look up a catalog entry by its canonical <see cref="ServiceCatalogEntry.Name"/>.</summary>
    public static ServiceCatalogEntry? FindByName(string name)
        => All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
