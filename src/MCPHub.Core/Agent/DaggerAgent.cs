using MCPHub.Core.Catalog;

namespace MCPHub.Core.Agent;

/// <summary>How a launched DaggerAgent instance is run.</summary>
public enum AgentRunMode
{
    /// <summary>Interactive REPL, launched in its own console window, independent of MCPHub.</summary>
    Cli,

    /// <summary><c>dagger serve</c> — the HTTP agent + web UI; MCPHub opens a browser to it.</summary>
    Web,

    /// <summary><c>dagger serve</c> with the ticket poller enabled (<c>Triggers:Enabled=true</c>).</summary>
    Jobs,
}

/// <summary>
/// Static description of the DaggerAgent product — a Wixely LLM agent that drives the MCPSharp suite
/// through MCPHub's proxy. It is distributed like the MCP servers
/// (<c>DaggerAgent-{os}-x64-{flavor}-v{ver}.zip</c>), but the executable is <c>dagger[.exe]</c> and the
/// config is <c>appsettings.json</c>, so it reuses the catalog-entry machinery via those overrides.
/// </summary>
public static class DaggerAgent
{
    /// <summary>Default port <c>dagger serve</c> listens on.</summary>
    public const int DefaultServePort = 5090;

    /// <summary>Unauthenticated health endpoint exposed by <c>serve</c> mode.</summary>
    public const string HealthPath = "/agent/healthz";

    /// <summary>Path of the agent web UI served by <c>serve</c> mode (the interactive agent page).</summary>
    public const string UiPath = "/agent/ui";

    /// <summary>Log-store key (and Logs-page label) for the agent's captured output.</summary>
    public const string LogKey = "DaggerAgent";

    /// <summary>Environment variable (DAGGER_ prefix, __ nesting) that toggles the background poller.</summary>
    public const string TriggersEnabledEnv = "DAGGER_Triggers__Enabled";

    /// <summary>Reuses the catalog-entry machinery (release lookup, asset naming, install) for the agent.</summary>
    public static ServiceCatalogEntry Catalog { get; } = new(
        Name: "DaggerAgent",
        RepoOwner: "Wixely",
        RepoName: "DaggerAgent",
        DisplayName: "Dagger Agent",
        Description: "LLM agent that drives the MCPSharp suite via the MCPHub proxy",
        DefaultPort: DefaultServePort,
        EnvPrefix: "DAGGER_",
        ExecutableBaseName: "dagger",
        ConfigFileNameOverride: "appsettings.json");
}
