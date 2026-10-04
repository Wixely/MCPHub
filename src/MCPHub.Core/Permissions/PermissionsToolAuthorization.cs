using MCPHub.Proxy;

namespace MCPHub.Core.Permissions;

/// <summary>
/// Enforces a <see cref="PermissionsConfiguration"/> at the proxy: the allow-list half of the hub's
/// tool policy.
///
/// <para>Unlike the feature policies it stacks with — agent management, recipe access — this one
/// answers for <em>every</em> server key rather than its own. The feature policies are vetoes on a
/// capability; this is the statement of who may use anything at all. Because
/// <see cref="CompositeToolAuthorization"/> requires every policy to agree, a management tool needs
/// both a grant here and its feature switch there, and neither can be worked around from the other
/// side.</para>
///
/// <para>A caller the proxy could not authenticate arrives as <see cref="TenantContext.Default"/>.
/// What happens then is <see cref="PermissionsConfiguration.AllowUnauthenticated"/>'s decision, and
/// its default of <see langword="true"/> is what keeps the single-user desktop hub working exactly
/// as it did: no keys issued, no tenants, everything visible.</para>
/// </summary>
public sealed class PermissionsToolAuthorization : IToolAuthorization, IToolAuthorizationDiagnostics
{
    private readonly IPermissionsConfigurationSource _source;

    public PermissionsToolAuthorization(IPermissionsConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName) =>
        Allowed(tenant, serverKey, exposedToolName);

    /// <inheritdoc />
    public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName) =>
        Allowed(tenant, serverKey, exposedToolName);

    /// <summary>No principal for the tenant and unauthenticated callers are refused.</summary>
    public const string UnauthenticatedCode = "permissions.unauthenticated";

    /// <summary>The tenant does not match any principal — typically one deleted mid-session.</summary>
    public const string UnknownPrincipalCode = "permissions.unknown_principal";

    /// <summary>The principal exists but is switched off.</summary>
    public const string PrincipalDisabledCode = "permissions.principal_disabled";

    /// <summary>The principal is live, but nothing in its grants covers this tool.</summary>
    public const string NoGrantCode = "permissions.no_grant";

    /// <inheritdoc />
    public ToolDenial? Explain(TenantContext tenant, string serverKey, string exposedToolName)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        // Derived from the same predicate that enforces, so an explanation can never claim a tool is
        // denied that this policy would allow, or the reverse.
        if (Allowed(tenant, serverKey, exposedToolName))
        {
            return null;
        }

        var configuration = _source.Snapshot;
        var principal = configuration.Principals.FirstOrDefault(
            p => string.Equals(p.Id, tenant.TenantId, StringComparison.Ordinal));

        if (principal is null)
        {
            return tenant.IsDefault
                ? new ToolDenial
                {
                    Code = UnauthenticatedCode,
                    Reason = "The caller presented no key that resolves to a principal, and this hub "
                             + "refuses unauthenticated callers.",
                    Remedy = "Issue the caller a key, or set AllowUnauthenticated to allow anonymous use.",
                }
                : new ToolDenial
                {
                    Code = UnknownPrincipalCode,
                    Reason = $"No principal has the id '{tenant.TenantId}'.",
                    Remedy = "The principal was probably deleted while the caller was connected. "
                             + "Re-create it, or have the caller reconnect with a current key.",
                };
        }

        if (!principal.Enabled)
        {
            return new ToolDenial
            {
                Code = PrincipalDisabledCode,
                Reason = $"Principal '{principal.Name}' is disabled, so none of its grants apply.",
                Remedy = "Enable the principal.",
            };
        }

        return new ToolDenial
        {
            Code = NoGrantCode,
            Reason = $"Principal '{principal.Name}' holds no grant covering '{exposedToolName}'.",
            Remedy = $"Grant '{exposedToolName}', or '{serverKey}"
                     + $"{PermissionsConfigurationRules.ServerWildcardSuffix}' for the whole server.",
        };
    }

    /// <summary>
    /// One decision for both questions, deliberately. The proxy filters <c>tools/list</c> with
    /// <see cref="IsToolVisible"/> and checks <see cref="IsCallAllowed"/> on the call, and a policy
    /// whose answers could differ would either advertise a tool it then refuses — which reads as a
    /// broken server — or hide one it would have run, which is worse.
    /// </summary>
    private bool Allowed(TenantContext tenant, string serverKey, string exposedToolName)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var configuration = _source.Snapshot;

        // The id the authenticator resolved, matched back to a principal. Matching by id rather than
        // re-hashing a key: the proxy never sees the key again after the handshake, and it should not
        // have to.
        foreach (var principal in configuration.Principals)
        {
            if (string.Equals(principal.Id, tenant.TenantId, StringComparison.Ordinal))
            {
                return PermissionsConfigurationRules.Grants(principal, serverKey, exposedToolName);
            }
        }

        // No principal for this tenant. Either nothing authenticated it, or its principal was deleted
        // mid-session — and a deleted principal must not keep working until it reconnects.
        return configuration.AllowUnauthenticated && tenant.IsDefault;
    }
}
