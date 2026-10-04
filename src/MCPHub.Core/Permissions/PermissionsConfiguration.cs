namespace MCPHub.Core.Permissions;

/// <summary>
/// Who may use the proxy, and which of its tools.
///
/// <para>The hub is the authority on this. A consumer — Banter, an editor, a script — presents a key
/// and is resolved to exactly one principal; the principal's grants then decide what appears in
/// <c>tools/list</c> at all. Nothing downstream needs to be trusted to filter, which is the point of
/// holding the policy here rather than in each consumer.</para>
///
/// <para>Shaped after <see cref="Routing.RouterConfiguration"/> deliberately: one record per
/// authenticated caller, each carrying a hashed key, validated by a rules class, and readable from
/// either desktop storage or a read-only deployment file. The router proved the shape; this is the
/// same shape applied to tools.</para>
/// </summary>
public sealed record PermissionsConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Whether a caller with no principal is allowed everything.
    ///
    /// <para><see langword="true"/> is the single-user default the hub has always had: the desktop app
    /// binds loopback, presents no key, and expects every tool. A deployment that issues keys sets
    /// this <see langword="false"/>, and then an unauthenticated caller gets nothing rather than
    /// everything — which is the only safe direction for that switch to fail.</para>
    /// </summary>
    public bool AllowUnauthenticated { get; init; } = true;

    public PermissionsPrincipal[] Principals { get; init; } = [];
}

/// <summary>
/// One authenticated caller and what it may reach.
///
/// <para>One key per principal, as <see cref="Routing.RouterInput"/> has one key per input: rotation
/// is replacing the hash, and revocation is <see cref="Enabled"/> or deletion. Several keys per
/// principal would be a nicer story for overlapping rotation and can be added without changing the
/// grant model, which is the part consumers depend on.</para>
/// </summary>
public sealed record PermissionsPrincipal
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>What an operator calls this caller — an agent's nickname, a service's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Off keeps the principal and its grants while refusing its key. Deleting is for a caller that
    /// is gone; this is for one that is suspended, and it keeps the audit trail legible.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>SHA-256 of the key, as 64 lowercase hex characters. The key itself is never stored.</summary>
    public string KeyHash { get; init; } = string.Empty;

    /// <summary>
    /// What this principal may see and call, as exposed (namespaced) tool names.
    ///
    /// <para>Three forms: an exact name (<c>kodi__play_pause</c>), every tool of one server
    /// (<c>kodi__*</c>), or everything (<c>*</c>). The last exists because of installation: a
    /// principal that may bring up servers must be able to use tools that do not exist yet, and
    /// enumerating them in advance is impossible. It is the widest grant there is, so an operator
    /// should be able to see at a glance that somebody holds it.</para>
    /// </summary>
    public string[] Tools { get; init; } = [];
}
