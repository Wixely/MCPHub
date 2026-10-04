using System.Text.Json.Serialization;

namespace MCPHub.Core.Permissions;

/// <summary>
/// What the <c>permissions__*</c> tools return.
///
/// <para>JSON records rather than prose, so a management client can render and branch on them. Every
/// failure carries a stable <see cref="PermissionsDenial.Code"/> for that reason: the wording of a
/// reason may improve, and a client matching on wording would break when it did.</para>
/// </summary>
public sealed record PermissionsStatus
{
    /// <summary>Whether a caller with no recognised key may use every tool.</summary>
    public bool AllowUnauthenticated { get; init; }

    /// <summary>
    /// Whether keys are actually being checked — the inverse of <see cref="AllowUnauthenticated"/>.
    ///
    /// <para>Its own field because the two together describe the trap: while this is false a caller
    /// presenting no key is served as the single user and gets everything, so users and their grants
    /// have no effect at all.</para>
    /// </summary>
    public bool KeysEnforced { get; init; }

    /// <summary>False when policy is mounted read-only. A client should grey its editing controls
    /// rather than let somebody make changes that cannot land.</summary>
    public bool Editable { get; init; }

    public int UserCount { get; init; }

    public int EnabledUserCount { get; init; }

    /// <summary>How many users have any grants. A gap between this and <see cref="EnabledUserCount"/>
    /// is users that can authenticate and then do nothing, which is usually a half-finished setup.</summary>
    public int GrantedUserCount { get; init; }

    /// <summary>Why the stored document was rejected at startup, when it was. No user is granted
    /// anything while this is set, so it explains a hub that refuses every tool at once.</summary>
    public string? LoadError { get; init; }

    /// <summary>Why the last reload of a mounted document was rejected. The previous policy is still
    /// in force, which is why this is worth reporting rather than hiding.</summary>
    public string? ReloadError { get; init; }

    /// <summary>Set when the policy is self-defeating in a way nothing else would report — see
    /// <see cref="KeysEnforced"/>. Not an error; it is a legitimate state to pass through while
    /// setting a hub up, and never one to stay in.</summary>
    public string? Warning { get; init; }
}

/// <summary>One user's grants, joined with enough of its identity to be legible.</summary>
public sealed record PermissionsGrantSummary
{
    public string UserId { get; init; } = string.Empty;

    /// <summary>The user's name, or empty when the grant names a user that no longer exists — which is
    /// itself worth seeing, since it is a grant doing nothing.</summary>
    public string UserName { get; init; } = string.Empty;

    /// <summary>Whether that user can authenticate at all. A grant on a suspended user applies to
    /// nothing.</summary>
    public bool UserEnabled { get; init; }

    /// <summary>False when no user has this id. The grant is inert and should probably be removed.</summary>
    public bool UserExists { get; init; }

    public string[] Tools { get; init; } = [];
}

public sealed record PermissionsGrantList
{
    public PermissionsGrantSummary[] Grants { get; init; } = [];
}

/// <summary>One reason a tool is unavailable. Mirrors <see cref="Proxy.ToolDenial"/> for the wire.</summary>
public sealed record PermissionsDenial
{
    public string Code { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? Remedy { get; init; }

    /// <summary>The environment variable forcing this, value included. Set, and an operator stops
    /// editing a setting that cannot win.</summary>
    public string? PinnedBy { get; init; }
}

/// <summary>
/// Why a tool is or is not available to a user.
///
/// <para><see cref="Denials"/> holds <em>every</em> reason rather than the first, which is the point of
/// the tool: a grant and a feature switch can both be withholding the same tool, and an operator who
/// fixes one and sees no change will conclude the fix did not work.</para>
/// </summary>
public sealed record PermissionsExplanation
{
    public string UserId { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string Tool { get; init; } = string.Empty;

    public string ServerKey { get; init; } = string.Empty;

    public bool Available { get; init; }

    public PermissionsDenial[] Denials { get; init; } = [];
}

public sealed record PermissionsChange
{
    public string Message { get; init; } = string.Empty;

    public PermissionsGrantSummary? Grant { get; init; }
}

/// <summary>Source-generated JSON for permissions tool results: indented, camelCase, nulls omitted —
/// the same shape <c>ManagementJsonContext</c> uses, so a client parsing one hub surface does not have
/// to switch conventions for another.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PermissionsStatus))]
[JsonSerializable(typeof(PermissionsGrantList))]
[JsonSerializable(typeof(PermissionsExplanation))]
[JsonSerializable(typeof(PermissionsChange))]
[JsonSerializable(typeof(PermissionsDenial))]
public sealed partial class PermissionsResultsJsonContext : JsonSerializerContext;
