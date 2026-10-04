using MCPHub.Core.Settings;
using MCPHub.Proxy;

namespace MCPHub.Core.Management;

/// <summary>
/// Decides what connected agents may do to MCPHub itself through the <c>mcphub__*</c> tools. One master
/// switch (off by default — an agent installing binaries and starting processes is something the user
/// opts into) and three capability switches beneath it: process control (start / stop / restart),
/// install / update, and update checks (servers and MCPHub). Each has a persisted setting and an
/// environment-variable override for headless / container deployments; an override wins and the UI shows
/// the checkbox locked. Config files and logs are deliberately not covered — no tool exposes them.
/// </summary>
public interface IAgentManagementPolicy
{
    /// <summary>Whether agents can see and call any <c>mcphub__*</c> tool (<c>mcphub__list_services</c> comes with this alone).</summary>
    bool ManagementEnabled { get; }

    /// <summary>Effective: agents may start, stop and restart servers (requires <see cref="ManagementEnabled"/>).</summary>
    bool ControlEnabled { get; }

    /// <summary>Effective: agents may install servers and apply updates (requires <see cref="ManagementEnabled"/>).</summary>
    bool InstallEnabled { get; }

    /// <summary>Effective: agents may check GitHub for server releases and for MCPHub itself (requires <see cref="ManagementEnabled"/>).</summary>
    bool UpdateChecksEnabled { get; }

    /// <summary>The control switch on its own, ignoring the master — what a checkbox shows while management is off.</summary>
    bool ControlSwitch { get; }

    /// <summary>The install switch on its own, ignoring the master.</summary>
    bool InstallSwitch { get; }

    /// <summary>The update-checks switch on its own, ignoring the master.</summary>
    bool UpdateChecksSwitch { get; }

    /// <summary>Set when <see cref="ManagementEnabled"/> comes from the environment rather than settings.</summary>
    string? ManagementEnabledOverrideSource { get; }

    /// <summary>Set when the control switch comes from the environment.</summary>
    string? ControlOverrideSource { get; }

    /// <summary>Set when the install switch comes from the environment.</summary>
    string? InstallOverrideSource { get; }

    /// <summary>Set when the update-checks switch comes from the environment.</summary>
    string? UpdateChecksOverrideSource { get; }

    /// <summary>Whether the un-namespaced tool (<c>start</c>, <c>install</c>, …) is currently allowed.</summary>
    bool IsToolEnabled(string toolName);

    /// <summary>MCP server instructions describing what the agent may currently do; <see langword="null"/> when management is off.</summary>
    string? ServerInstructions { get; }
}

/// <summary>
/// <see cref="IAgentManagementPolicy"/> backed by <see cref="MCPHubSettings"/> with environment overrides, and
/// the <see cref="IToolAuthorization"/> that enforces it on the proxy: <c>mcphub__*</c> tools an agent may not
/// use are filtered out of <c>tools/list</c> and refused on call. Every other server's tools are allowed —
/// stack this with other feature policies through <see cref="CompositeToolAuthorization"/>.
/// </summary>
public sealed class AgentManagementPolicy : IAgentManagementPolicy, IToolAuthorization, IToolAuthorizationDiagnostics
{
    /// <summary>Environment variable that forces the whole feature on/off.</summary>
    public const string EnabledVariable = "MCPHUB_AGENT_MANAGEMENT_ENABLED";

    /// <summary>Environment variable that forces start / stop / restart on/off.</summary>
    public const string ControlVariable = "MCPHUB_AGENT_MANAGEMENT_CONTROL";

    /// <summary>Environment variable that forces install / update on/off.</summary>
    public const string InstallVariable = "MCPHUB_AGENT_MANAGEMENT_INSTALL";

    /// <summary>Environment variable that forces update checks on/off.</summary>
    public const string UpdateChecksVariable = "MCPHUB_AGENT_MANAGEMENT_UPDATE_CHECKS";

    private readonly ISettingsStore _settings;
    private readonly Func<string, string?> _environment;

    public AgentManagementPolicy(ISettingsStore settings)
        : this(settings, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam: <paramref name="environment"/> stands in for <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public AgentManagementPolicy(ISettingsStore settings, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);
        _settings = settings;
        _environment = environment;
    }

    /// <inheritdoc />
    public bool ManagementEnabled => Override(EnabledVariable) ?? _settings.Current.AgentManagementEnabled;

    /// <inheritdoc />
    public bool ControlEnabled => ManagementEnabled && ControlSwitch;

    /// <inheritdoc />
    public bool InstallEnabled => ManagementEnabled && InstallSwitch;

    /// <inheritdoc />
    public bool UpdateChecksEnabled => ManagementEnabled && UpdateChecksSwitch;

    /// <inheritdoc />
    public bool ControlSwitch => Override(ControlVariable) ?? _settings.Current.AgentManagementControlEnabled;

    /// <inheritdoc />
    public bool InstallSwitch => Override(InstallVariable) ?? _settings.Current.AgentManagementInstallEnabled;

    /// <inheritdoc />
    public bool UpdateChecksSwitch => Override(UpdateChecksVariable) ?? _settings.Current.AgentManagementUpdateChecksEnabled;

    /// <inheritdoc />
    public string? ManagementEnabledOverrideSource => EnvironmentFlag.Describe(EnabledVariable, _environment(EnabledVariable));

    /// <inheritdoc />
    public string? ControlOverrideSource => EnvironmentFlag.Describe(ControlVariable, _environment(ControlVariable));

    /// <inheritdoc />
    public string? InstallOverrideSource => EnvironmentFlag.Describe(InstallVariable, _environment(InstallVariable));

    /// <inheritdoc />
    public string? UpdateChecksOverrideSource => EnvironmentFlag.Describe(UpdateChecksVariable, _environment(UpdateChecksVariable));

    /// <inheritdoc />
    public bool IsToolEnabled(string toolName)
    {
        if (!ManagementEnabled)
            return false;
        if (string.Equals(toolName, AgentManagementToolProvider.ListTool, StringComparison.Ordinal))
            return true;
        if (AgentManagementToolProvider.ControlTools.Contains(toolName))
            return ControlEnabled;
        if (AgentManagementToolProvider.InstallTools.Contains(toolName))
            return InstallEnabled;
        if (AgentManagementToolProvider.UpdateCheckTools.Contains(toolName))
            return UpdateChecksEnabled;
        return false;
    }

    /// <inheritdoc />
    public string? ServerInstructions => ManagementEnabled
        ? AgentManagementToolProvider.BuildServerInstructions(ControlEnabled, InstallEnabled, UpdateChecksEnabled)
        : null;

    /// <inheritdoc />
    public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName)
        => IsAllowed(serverKey, exposedToolName);

    /// <inheritdoc />
    public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName)
        => IsAllowed(serverKey, exposedToolName);

    private bool IsAllowed(string serverKey, string exposedToolName)
    {
        if (!string.Equals(serverKey, AgentManagementToolProvider.ProviderKey, StringComparison.Ordinal))
            return true;

        var prefix = AgentManagementToolProvider.ProviderKey + ProxyConstants.NamespaceSeparator;
        var original = exposedToolName.StartsWith(prefix, StringComparison.Ordinal) ? exposedToolName[prefix.Length..] : exposedToolName;
        return IsToolEnabled(original);
    }

    /// <summary>Agent management is off altogether, so no <c>mcphub__*</c> tool is available.</summary>
    public const string ManagementOffCode = "agent_management.off";

    /// <summary>Management is on, but the capability this tool belongs to is off.</summary>
    public const string CapabilityOffCode = "agent_management.capability_off";

    /// <summary>The tool is not one this policy knows, so it cannot be enabled.</summary>
    public const string UnknownToolCode = "agent_management.unknown_tool";

    /// <inheritdoc />
    public ToolDenial? Explain(TenantContext tenant, string serverKey, string exposedToolName)
    {
        // Same predicate the policy enforces with: an explanation that disagreed with enforcement
        // would send an operator after the wrong switch.
        if (IsAllowed(serverKey, exposedToolName))
            return null;

        var prefix = AgentManagementToolProvider.ProviderKey + ProxyConstants.NamespaceSeparator;
        var tool = exposedToolName.StartsWith(prefix, StringComparison.Ordinal) ? exposedToolName[prefix.Length..] : exposedToolName;

        if (!ManagementEnabled)
            return new ToolDenial
            {
                Code = ManagementOffCode,
                Reason = "Letting agents manage servers is off, so no mcphub__ tool is available.",
                Remedy = $"Turn on Settings → Agent management, or set {EnabledVariable}=true.",
                PinnedBy = ManagementEnabledOverrideSource,
            };

        // Management is on and this specific capability is not. Naming the capability AND the variable
        // pinning it is the whole point: without both, an operator toggles a checkbox that an
        // environment variable immediately overrides, and nothing says so.
        var (capability, variable, pinnedBy) =
            AgentManagementToolProvider.ControlTools.Contains(tool)
                ? ("Start, stop and restart servers", ControlVariable, ControlOverrideSource)
                : AgentManagementToolProvider.InstallTools.Contains(tool)
                    ? ("Install and update servers", InstallVariable, InstallOverrideSource)
                    : AgentManagementToolProvider.UpdateCheckTools.Contains(tool)
                        ? ("Check GitHub for updates", UpdateChecksVariable, UpdateChecksOverrideSource)
                        : (null, null, null);

        if (capability is null)
            return new ToolDenial
            {
                Code = UnknownToolCode,
                Reason = $"'{tool}' is not an agent-management tool this version knows.",
                Remedy = "Check the tool name against the hub's version.",
            };

        return new ToolDenial
        {
            Code = CapabilityOffCode,
            Reason = $"Agent management is on, but '{capability}' is off — which is what '{tool}' needs.",
            Remedy = $"Turn on that switch, or set {variable}=true.",
            PinnedBy = pinnedBy,
        };
    }

    private bool? Override(string variable) => EnvironmentFlag.Parse(_environment(variable));
}
