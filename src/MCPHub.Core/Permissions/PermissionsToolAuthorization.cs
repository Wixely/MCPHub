using MCPHub.Core.Users;
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
    private readonly IUserDirectory _users;

    /// <param name="users">Who the caller is, and whether it is still allowed in. Asked on every call
    /// rather than trusted from the handshake, so suspending or deleting a user takes effect at once
    /// instead of when it next reconnects.</param>
    /// <param name="source">What that user may use.</param>
    public PermissionsToolAuthorization(IUserDirectory users, IPermissionsConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(source);
        _users = users;
        _source = source;
    }

    /// <inheritdoc />
    public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName) =>
        Allowed(tenant, serverKey, exposedToolName);

    /// <inheritdoc />
    public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName) =>
        Allowed(tenant, serverKey, exposedToolName);

    /// <summary>No user for the tenant, and unauthenticated callers are refused.</summary>
    public const string UnauthenticatedCode = "permissions.unauthenticated";

    /// <summary>The tenant does not match any user — typically one deleted mid-session.</summary>
    public const string UnknownUserCode = "permissions.unknown_user";

    /// <summary>The user exists but is suspended.</summary>
    public const string UserDisabledCode = "permissions.user_disabled";

    /// <summary>The user is live, but nothing in its grants covers this tool.</summary>
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

        var user = _users.Snapshot.Users
            .FirstOrDefault(u => string.Equals(u.Id, tenant.TenantId, StringComparison.Ordinal));

        if (user is null)
        {
            return tenant.IsDefault
                ? new ToolDenial
                {
                    Code = UnauthenticatedCode,
                    Reason = "The caller presented no key that resolves to a user, and this hub refuses "
                             + "unauthenticated callers.",
                    Remedy = "Issue the caller a key, or allow unauthenticated callers.",
                }
                : new ToolDenial
                {
                    Code = UnknownUserCode,
                    Reason = $"No user has the id '{tenant.TenantId}'.",
                    Remedy = "The user was probably deleted while the caller was connected. Re-create it, "
                             + "or have the caller reconnect with a current key.",
                };
        }

        if (!user.Enabled)
        {
            return new ToolDenial
            {
                Code = UserDisabledCode,
                Reason = $"User '{user.Name}' is suspended, so none of its grants apply.",
                Remedy = "Enable the user.",
            };
        }

        return new ToolDenial
        {
            Code = NoGrantCode,
            Reason = $"User '{user.Name}' holds no grant covering '{exposedToolName}'.",
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

        // Matched by the id the authenticator resolved, not by re-checking a key: the proxy never sees
        // the key again after the handshake, and it should not have to.
        var user = _users.Snapshot.Users
            .FirstOrDefault(u => string.Equals(u.Id, tenant.TenantId, StringComparison.Ordinal));

        if (user is null)
        {
            // Either nothing authenticated this caller, or its user was deleted mid-session — and a
            // deleted user must not keep working until it reconnects.
            return _source.Snapshot.AllowUnauthenticated && tenant.IsDefault;
        }

        if (!user.Enabled)
        {
            return false;
        }

        return _source.GrantsFor(user.Id) is { } grant
               && PermissionsConfigurationRules.Covers(grant, serverKey, exposedToolName);
    }
}
