using System.Text.Json.Serialization;
using MCPHub.Core.Recipes;
using MCPHub.Core.Routing;
using MCPHub.Core.Settings;

namespace MCPHub.Core.Backup;

/// <summary>
/// A selectable slice of MCPHub's configuration. Each maps to one entry in the archive, so a user can take
/// the parts that travel (routes, servers, recipes) and leave the parts that do not (a machine's own folders).
/// </summary>
public enum SettingsCategory
{
    /// <summary>Servers folder, download flavour, proxy port and bind, launch and tray behaviour.</summary>
    General,

    /// <summary>User-added MCP servers on the Proxy page, without their tokens (those are <see cref="Secrets"/>).</summary>
    UserServers,

    /// <summary>DaggerAgent and Slopworks folders, auto-start choices, and the agent-management switches.</summary>
    AgentAndEngine,

    /// <summary>The recipes knowledge base.</summary>
    Recipes,

    /// <summary>Model Router listener, outputs and agents. Upstream keys travel only in an encrypted archive.</summary>
    Router,

    /// <summary>Window size and theme.</summary>
    Appearance,

    /// <summary>
    /// The GitHub token, user-server tokens and Router upstream keys. Protected on disk by the current
    /// Windows user, so exporting them means re-wrapping them — which this archive only does under a password.
    /// </summary>
    Secrets,
}

/// <summary>Everything about an archive that can be read without the password.</summary>
public sealed record SettingsArchiveManifest
{
    /// <summary>Bumped only for a change that older readers could not interpret correctly.</summary>
    public int SchemaVersion { get; init; } = 1;

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>MCPHub version that wrote the archive, for diagnosing an import that behaves oddly.</summary>
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>Categories actually present, in the order they were written.</summary>
    public SettingsCategory[] Categories { get; init; } = [];

    /// <summary>Whether entries are encrypted and therefore need the password to read.</summary>
    public bool Encrypted { get; init; }

    /// <summary>Base64 PBKDF2 salt. Present only when <see cref="Encrypted"/>.</summary>
    public string? KdfSalt { get; init; }

    /// <summary>PBKDF2-HMAC-SHA256 iteration count used to derive the key.</summary>
    public int KdfIterations { get; init; }
}

/// <summary>What an archive holds for <see cref="SettingsCategory.General"/>.</summary>
public sealed record GeneralSection
{
    public string? SharedServersFolder { get; init; }
    public string Flavor { get; init; } = nameof(Models.PublishFlavor.SelfContained);
    public int ProxyPort { get; init; }
    public string ProxyBindAddress { get; init; } = "127.0.0.1";
    public bool StartProxyOnLaunch { get; init; }
    public bool MinimizeToTray { get; init; }
    public bool CloseToTray { get; init; }
    public List<string> AutoStartServices { get; init; } = [];
}

/// <summary>What an archive holds for <see cref="SettingsCategory.AgentAndEngine"/>.</summary>
public sealed record AgentAndEngineSection
{
    public string? AgentFolder { get; init; }
    public string? SlopworksFolder { get; init; }
    public bool AutoStartAgentCli { get; init; }
    public bool AutoStartAgentWeb { get; init; }
    public bool AutoStartAgentJobs { get; init; }
    public bool AgentServeBindAllInterfaces { get; init; }
    public bool AutoStartSlopworks { get; init; }
    public bool RecipesEnabled { get; init; }
    public bool RecipesAgentEditEnabled { get; init; }
    public bool AgentManagementEnabled { get; init; }
    public bool AgentManagementControlEnabled { get; init; }
    public bool AgentManagementInstallEnabled { get; init; }
    public bool AgentManagementUpdateChecksEnabled { get; init; }
}

/// <summary>What an archive holds for <see cref="SettingsCategory.Appearance"/>.</summary>
public sealed record AppearanceSection
{
    public string Theme { get; init; } = "Default";
    public double WindowWidth { get; init; }
    public double WindowHeight { get; init; }
}

/// <summary>User-added MCP servers, exported as their stored definitions (tokens excluded by design).</summary>
public sealed record UserServersSection
{
    public List<UserMcpServerDefinition> Servers { get; init; } = [];
}

/// <summary>The recipes knowledge base.</summary>
public sealed record RecipesSection
{
    public List<Recipe> Recipes { get; init; } = [];
}

/// <summary>
/// Router routes. Output credentials are carried separately in <see cref="SecretsSection"/> so that an
/// unencrypted archive can still move a topology between machines without leaking any key.
/// </summary>
public sealed record RouterSection
{
    public int Port { get; init; } = 5801;
    public string BindAddress { get; init; } = RouterConfigurationRules.Loopback;
    public bool StartOnLaunch { get; init; }
    public string? DefaultOutputId { get; init; }

    /// <summary>Outputs with <c>ProtectedApiKey</c> cleared; the plaintext key, if exported, is in the secrets section.</summary>
    public List<RouterOutput> Outputs { get; init; } = [];

    /// <summary>Agents, including their key hashes, so existing agent keys keep working after an import.</summary>
    public List<RouterInput> Inputs { get; init; } = [];
}

/// <summary>
/// Plaintext credentials. Only ever written into an encrypted archive — <see cref="SettingsArchiveService"/>
/// refuses to export this category without a password.
/// </summary>
public sealed record SecretsSection
{
    /// <summary>Secret-store entries by key, e.g. <c>github_pat</c> and <c>user_server_token_*</c>.</summary>
    public Dictionary<string, string> Store { get; init; } = [];

    /// <summary>Router output upstream keys by output id, unwrapped from their at-rest protection.</summary>
    public Dictionary<string, string> RouterOutputKeys { get; init; } = [];
}

/// <summary>Source-generated JSON for archive entries. Indented so an unencrypted archive is readable.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SettingsArchiveManifest))]
[JsonSerializable(typeof(GeneralSection))]
[JsonSerializable(typeof(AgentAndEngineSection))]
[JsonSerializable(typeof(AppearanceSection))]
[JsonSerializable(typeof(UserServersSection))]
[JsonSerializable(typeof(RecipesSection))]
[JsonSerializable(typeof(RouterSection))]
[JsonSerializable(typeof(SecretsSection))]
internal sealed partial class SettingsArchiveJsonContext : JsonSerializerContext;
