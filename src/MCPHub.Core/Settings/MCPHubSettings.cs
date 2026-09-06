using System.Linq;
using System.Text.Json.Serialization;
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

/// <summary>
/// How MCPHub presents a credential to a user-added MCP server. The token itself is never part of
/// the settings file — only the delivery mechanism is; see <see cref="UserMcpServerDefinition.SecretKey"/>.
/// </summary>
public enum McpAuthKind
{
    /// <summary>No credential is sent.</summary>
    None,

    /// <summary>HTTP: sent as <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    BearerToken,

    /// <summary>
    /// HTTP: sent verbatim (no scheme prefix) as the header named by
    /// <see cref="UserMcpServerDefinition.AuthHeaderName"/>, e.g. <c>X-API-Key</c>.
    /// </summary>
    HeaderToken,

    /// <summary>
    /// stdio: passed to the child process in the environment variable named by
    /// <see cref="UserMcpServerDefinition.AuthEnvironmentVariable"/>. Nothing is put on the command line,
    /// where it would be visible to any process listing.
    /// </summary>
    EnvironmentToken,
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

    /// <summary>
    /// How this server's token is presented, if it has one. The token lives in the secret store under
    /// <see cref="SecretKey"/> — deliberately never in this object, so it cannot reach <c>settings.json</c>.
    /// </summary>
    public McpAuthKind Auth { get; set; } = McpAuthKind.None;

    /// <summary>Header carrying the token when <see cref="Auth"/> is <see cref="McpAuthKind.HeaderToken"/>; blank means <c>X-API-Key</c>.</summary>
    public string? AuthHeaderName { get; set; }

    /// <summary>Environment variable carrying the token when <see cref="Auth"/> is <see cref="McpAuthKind.EnvironmentToken"/>; blank means <c>MCP_AUTH_TOKEN</c>.</summary>
    public string? AuthEnvironmentVariable { get; set; }

    /// <summary>Secret-store key holding this server's token. Derived from <see cref="Id"/>; never serialised.</summary>
    [JsonIgnore]
    public string SecretKey => SecretKeys.UserServerToken(Id);

    /// <summary>Header actually used for <see cref="McpAuthKind.HeaderToken"/>, applying the default.</summary>
    [JsonIgnore]
    public string EffectiveAuthHeaderName =>
        string.IsNullOrWhiteSpace(AuthHeaderName) ? DefaultAuthHeaderName : AuthHeaderName.Trim();

    /// <summary>Environment variable actually used for <see cref="McpAuthKind.EnvironmentToken"/>, applying the default.</summary>
    [JsonIgnore]
    public string EffectiveAuthEnvironmentVariable =>
        string.IsNullOrWhiteSpace(AuthEnvironmentVariable) ? DefaultAuthEnvironmentVariable : AuthEnvironmentVariable.Trim();

    /// <summary>Header used when <see cref="McpAuthKind.HeaderToken"/> is chosen without naming one.</summary>
    public const string DefaultAuthHeaderName = "X-API-Key";

    /// <summary>Environment variable used when <see cref="McpAuthKind.EnvironmentToken"/> is chosen without naming one.</summary>
    public const string DefaultAuthEnvironmentVariable = "MCP_AUTH_TOKEN";

    /// <summary>Stable namespacing key for the proxy, e.g. <c>user-myserver</c>.</summary>
    [JsonIgnore]
    public string Key
    {
        get
        {
            var slug = new string((DisplayName ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return "user-" + (string.IsNullOrEmpty(slug) ? Id : slug);
        }
    }
}

/// <summary>
/// Turns a user-added server's declared <see cref="McpAuthKind"/> plus its stored token into the headers or
/// environment the transport actually needs. Pure and token-in/token-out so it can be exercised without a
/// secret store; the caller is responsible for fetching the token.
/// </summary>
public static class UserServerAuth
{
    /// <summary>
    /// HTTP headers carrying <paramref name="token"/>, or <see langword="null"/> when this server sends no
    /// header credential — including when the token is absent, so a bare <c>Bearer </c> is never sent.
    /// </summary>
    public static Dictionary<string, string>? BuildHeaders(UserMcpServerDefinition definition, string? token)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrEmpty(token))
            return null;

        return definition.Auth switch
        {
            McpAuthKind.BearerToken => new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token },
            McpAuthKind.HeaderToken => new(StringComparer.OrdinalIgnoreCase) { [definition.EffectiveAuthHeaderName] = token },
            _ => null,
        };
    }

    /// <summary>
    /// Environment carrying <paramref name="token"/> for a stdio server, or <see langword="null"/> when this
    /// server sends no environment credential.
    /// </summary>
    public static Dictionary<string, string?>? BuildEnvironment(UserMcpServerDefinition definition, string? token)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrEmpty(token) || definition.Auth != McpAuthKind.EnvironmentToken)
            return null;

        return new(StringComparer.Ordinal) { [definition.EffectiveAuthEnvironmentVariable] = token };
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

    /// <summary>Auto-start the Slopworks vLLM server (<c>Slopworks.App.exe start</c>) when MCPHub launches.</summary>
    public bool AutoStartSlopworks { get; set; }

    /// <summary>
    /// Whether agents can see and call the <c>recipes__*</c> tools at all. Off hides the whole knowledge base
    /// from the proxy; the Recipes page itself is unaffected. Overridable with <c>MCPHUB_RECIPES_ENABLED</c>.
    /// </summary>
    public bool RecipesEnabled { get; set; } = true;

    /// <summary>
    /// Whether agents may add, update and remove recipes (off leaves <c>recipes__list</c> / <c>get</c> only).
    /// Overridable with <c>MCPHUB_RECIPES_AGENT_EDIT</c>.
    /// </summary>
    public bool RecipesAgentEditEnabled { get; set; } = true;

    /// <summary>
    /// Whether agents get the <c>mcphub__*</c> management tools at all (list servers, and whichever of the three
    /// capabilities below are on). Off by default: an agent installing binaries and starting processes is
    /// something the user opts into. Overridable with <c>MCPHUB_AGENT_MANAGEMENT_ENABLED</c>.
    /// </summary>
    public bool AgentManagementEnabled { get; set; }

    /// <summary>Agents may start, stop and restart managed servers. Overridable with <c>MCPHUB_AGENT_MANAGEMENT_CONTROL</c>.</summary>
    public bool AgentManagementControlEnabled { get; set; } = true;

    /// <summary>Agents may install managed servers and apply updates. Overridable with <c>MCPHUB_AGENT_MANAGEMENT_INSTALL</c>.</summary>
    public bool AgentManagementInstallEnabled { get; set; } = true;

    /// <summary>Agents may check GitHub for server releases and for MCPHub itself. Overridable with <c>MCPHUB_AGENT_MANAGEMENT_UPDATE_CHECKS</c>.</summary>
    public bool AgentManagementUpdateChecksEnabled { get; set; } = true;
}
