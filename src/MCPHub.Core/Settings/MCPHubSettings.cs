using System.Linq;
using MCPHub.Core.Models;

namespace MCPHub.Core.Settings;

/// <summary>Transport used by a user-added MCP server.</summary>
public enum McpTransportKind
{
    /// <summary>Remote HTTP / Streamable HTTP endpoint.</summary>
    Http,

    /// <summary>Local child process spoken to over stdio.</summary>
    Stdio,
}

/// <summary>A user-defined MCP server (beyond the Wixely catalog) for the proxy to aggregate.</summary>
public sealed class UserMcpServerDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = string.Empty;
    public McpTransportKind Kind { get; set; } = McpTransportKind.Http;

    /// <summary>HTTP endpoint (for <see cref="McpTransportKind.Http"/>), e.g. <c>http://localhost:1234/mcp</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Command to launch (for <see cref="McpTransportKind.Stdio"/>).</summary>
    public string? Command { get; set; }

    public List<string> Arguments { get; set; } = [];

    public bool Enabled { get; set; } = true;

    /// <summary>Stable namespacing key for the proxy, e.g. <c>user-myserver</c>.</summary>
    public string Key
    {
        get
        {
            var slug = new string((DisplayName ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return "user-" + (string.IsNullOrEmpty(slug) ? Id : slug);
        }
    }
}

/// <summary>MCPHub's own persisted settings (excluding secrets, which live in the secret store).</summary>
public sealed class MCPHubSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Shared folder holding all sub-server exes + configs; <see langword="null"/> = default.</summary>
    public string? SharedServersFolder { get; set; }

    public PublishFlavor Flavor { get; set; } = PublishFlavor.SelfContained;

    public int ProxyPort { get; set; } = 5800;

    public string ProxyBindAddress { get; set; } = "127.0.0.1";

    public bool StartProxyOnLaunch { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    /// <summary>UI theme: <c>Default</c>, <c>Light</c>, or <c>Dark</c>.</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>Remembered main-window size (position is intentionally not persisted).</summary>
    public double WindowWidth { get; set; } = 1240;

    public double WindowHeight { get; set; } = 680;

    public List<UserMcpServerDefinition> UserServers { get; set; } = [];

    /// <summary>Catalog names of services MCPHub starts automatically on launch (per-service "auto-run").</summary>
    public List<string> AutoStartServices { get; set; } = [];

    /// <summary>Dedicated folder DaggerAgent is installed into; <see langword="null"/> = default (<c>{Data}/agent</c>).</summary>
    public string? AgentFolder { get; set; }

    /// <summary>Dedicated folder Slopworks is installed into; <see langword="null"/> = default (<c>{Data}/slopworks</c>).</summary>
    public string? SlopworksFolder { get; set; }

    /// <summary>Auto-start DaggerAgent's interactive CLI (REPL) when MCPHub launches.</summary>
    public bool AutoStartAgentCli { get; set; }

    /// <summary>Auto-start DaggerAgent in Web (serve) mode when MCPHub launches.</summary>
    public bool AutoStartAgentWeb { get; set; }

    /// <summary>Auto-start DaggerAgent in Jobs (serve + poller) mode when MCPHub launches.</summary>
    public bool AutoStartAgentJobs { get; set; }

    /// <summary>
    /// Bind DaggerAgent's <c>serve</c> to <c>0.0.0.0</c> (all interfaces, LAN-reachable) instead of
    /// loopback only. MCPHub still health-probes and opens the UI on <c>127.0.0.1</c> either way.
    /// </summary>
    public bool AgentServeBindAllInterfaces { get; set; }
}
