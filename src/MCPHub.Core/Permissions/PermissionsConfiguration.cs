namespace MCPHub.Core.Permissions;

/// <summary>
/// Which tools each user may use.
///
/// <para>Grants only — identity lives in <see cref="Users.HubUser"/>. This document says what a caller
/// may do; the user directory says who it is and whether it is still allowed in at all. They were one
/// thing until users existed, which is why a caller needed a separate key for every surface.</para>
/// </summary>
public sealed record PermissionsConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Whether a caller with no recognised key is allowed every tool.
    ///
    /// <para><see langword="true"/> is the single-user default the hub has always had: it binds
    /// loopback, presents no key, and expects everything. A deployment that issues keys sets this
    /// <see langword="false"/>, and then an unauthenticated caller gets nothing rather than
    /// everything — the only safe direction for that switch to fail.</para>
    /// </summary>
    public bool AllowUnauthenticated { get; init; } = true;

    public PermissionsGrant[] Grants { get; init; } = [];
}

/// <summary>
/// What one user may use. A user with no entry here may use nothing, which is why absence is the
/// default rather than something to configure.
/// </summary>
public sealed record PermissionsGrant
{
    /// <summary>The <see cref="Users.HubUser.Id"/> this applies to.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>
    /// Exposed (namespaced) tool names this user may see and call.
    ///
    /// <para>Three forms: an exact name (<c>kodi__play_pause</c>), every tool of one server
    /// (<c>kodi__*</c>), or everything (<c>*</c>). The last exists because of installation — a caller
    /// that may bring servers up must be able to use tools that do not exist when the grant is
    /// written, and enumerating them in advance is impossible. It is the widest grant there is, and it
    /// covers the administration tools too, so an operator should be able to see at a glance who holds
    /// it.</para>
    /// </summary>
    public string[] Tools { get; init; } = [];
}
