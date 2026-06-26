namespace MCPHub.Core.Catalog;

/// <summary>
/// The fixed set of 11 Wixely MCPSharp products MCPHub manages.
/// </summary>
/// <remarks>
/// Ports for Noteworthy (5710), SQL (5712) and GitHub (5701) are confirmed; the rest are
/// <see langword="null"/> and resolved from each installed <c>{Name}.json</c> at runtime
/// rather than hard-coded. Env-var prefixes follow the observed pattern (product name minus the
/// trailing "Sharp", upper-cased, plus "_") and are verified against the installed config later.
/// </remarks>
public static class ServiceCatalog
{
    /// <summary>All catalog entries, in display order.</summary>
    public static IReadOnlyList<ServiceCatalogEntry> All { get; } =
    [
        new("NoteworthyMCPSharp", "Wixely", "NoteworthyMCPSharp",
            "Noteworthy", "MIDI / notes library MCP server", 5710, "NOTEWORTHYMCP_"),

        new("SQLMCPSharp", "Wixely", "SQLMCPSharp",
            "SQL", "SQL databases (MSSQL, MySQL, …) MCP server", 5712, "SQLMCP_"),

        new("GithubMCPSharp", "Wixely", "GithubMCPSharp",
            "GitHub", "GitHub repositories & issues MCP server", 5701, "GITHUBMCP_"),

        new("GitlabMCPSharp", "Wixely", "GitlabMCPSharp",
            "GitLab", "GitLab projects & merge requests MCP server", null, "GITLABMCP_"),

        new("AzureDevopsMCPSharp", "Wixely", "AzureDevopsMCPSharp",
            "Azure DevOps", "Azure DevOps boards & repos MCP server", null, "AZUREDEVOPSMCP_"),

        new("HomeAssistantMCPSharp", "Wixely", "HomeAssistantMCPSharp",
            "Home Assistant", "Home Assistant smart-home MCP server", null, "HOMEASSISTANTMCP_"),

        new("PaperlessNgxMCPSharp", "Wixely", "PaperlessNgxMCPSharp",
            "Paperless-ngx", "Paperless-ngx document store MCP server", null, "PAPERLESSNGXMCP_"),

        new("ProxmoxMCPSharp", "Wixely", "ProxmoxMCPSharp",
            "Proxmox", "Proxmox VE virtualization MCP server", null, "PROXMOXMCP_"),

        new("RouterOSMCPSharp", "Wixely", "RouterOSMCPSharp",
            "RouterOS", "MikroTik RouterOS MCP server", null, "ROUTEROSMCP_"),

        new("ChromeDevToolsMCPSharp", "Wixely", "ChromeDevToolsMCPSharp",
            "Chrome DevTools", "Chrome DevTools Protocol MCP server", null, "CHROMEDEVTOOLSMCP_"),

        new("PlaywrightMCPSharp", "Wixely", "PlaywrightMCPSharp",
            "Playwright", "Playwright browser automation MCP server", null, "PLAYWRIGHTMCP_"),
    ];

    /// <summary>Look up a catalog entry by its canonical <see cref="ServiceCatalogEntry.Name"/>.</summary>
    public static ServiceCatalogEntry? FindByName(string name)
        => All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
