using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Proxy;

namespace MCPHub.Core.Users;

/// <summary>
/// Whether a caller gets the hub's administration tools at all — <c>users__*</c> and
/// <c>permissions__*</c>.
///
/// <para>One switch, off by default, with an environment override for headless deployments — the same
/// shape as <see cref="Management.AgentManagementPolicy"/>, for a stronger reason. Agent management is
/// opt-in because an agent installing binaries is something a user should choose. These tools govern
/// every other tool, so a caller holding them can grant itself anything: without a separate switch,
/// the widest grant (<c>*</c>) would silently amount to administrator, and an operator granting an
/// agent "everything" so it could install a server would also be handing it the policy.</para>
///
/// <para>Stacked through <see cref="CompositeToolAuthorization"/> like the other feature policies, so
/// a <c>permissions__*</c> tool needs both this switch and an explicit grant. Both conditions are
/// reported when both are missing — see <see cref="CompositeToolAuthorization.ExplainAll"/>.</para>
/// </summary>
public sealed class AdministrationPolicy : IToolAuthorization, IToolAuthorizationDiagnostics
{
    /// <summary>Environment variable that forces the permissions tools on or off.</summary>
    public const string EnabledVariable = "MCPHUB_ADMINISTRATION_ENABLED";

    /// <summary>The permissions tools are switched off for this hub.</summary>
    public const string AdministrationOffCode = "administration.off";

    private readonly ISettingsStore _settings;
    private readonly Func<string, string?> _environment;

    public AdministrationPolicy(ISettingsStore settings)
        : this(settings, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Test seam: <paramref name="environment"/> stands in for the real environment.</summary>
    public AdministrationPolicy(ISettingsStore settings, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(environment);
        _settings = settings;
        _environment = environment;
    }

    /// <summary>Whether the administration tools are available to anyone.</summary>
    public bool AdministrationEnabled =>
        EnvironmentFlag.Parse(_environment(EnabledVariable)) ?? _settings.Current.AdministrationEnabled;

    /// <summary>Set when the switch comes from the environment rather than from settings.</summary>
    public string? OverrideSource => EnvironmentFlag.Describe(EnabledVariable, _environment(EnabledVariable));

    /// <inheritdoc />
    public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName) => IsAllowed(serverKey);

    /// <inheritdoc />
    public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName) => IsAllowed(serverKey);

    /// <inheritdoc />
    public ToolDenial? Explain(TenantContext tenant, string serverKey, string exposedToolName) =>
        IsAllowed(serverKey)
            ? null
            : new ToolDenial
            {
                Code = AdministrationOffCode,
                Reason = "Administering this hub through the proxy is off, so no users__ or permissions__ "
                         + "tool is available.",
                Remedy = $"Set {EnabledVariable}=true, or turn it on in MCPHub's settings.",
                PinnedBy = OverrideSource,
            };

    /// <summary>Answers only for its own provider key and allows every other server's tools, which is
    /// what lets feature policies stack without knowing about each other.</summary>
    private bool IsAllowed(string serverKey) =>
        serverKey is not (UserToolProvider.ProviderKey or PermissionsToolProvider.ProviderKey)
        || AdministrationEnabled;
}
