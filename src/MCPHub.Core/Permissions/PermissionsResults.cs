using System.Text.Json.Serialization;

namespace MCPHub.Core.Permissions;

/// <summary>
/// What the <c>permissions__*</c> tools return.
///
/// <para>JSON records rather than prose, so a management UI can render and branch on them. Every
/// failure carries a stable <see cref="PermissionsDenial.Code"/> for exactly that reason: the wording
/// of a reason may improve, and a client matching on wording would break when it did.</para>
/// </summary>
public sealed record PermissionsStatus
{
    /// <summary>Whether a caller with no recognised key may use every tool.</summary>
    public bool AllowUnauthenticated { get; init; }

    /// <summary>
    /// False when policy is mounted read-only. A management UI should grey its editing controls rather
    /// than let somebody make changes that cannot land.
    /// </summary>
    public bool Editable { get; init; }

    public int PrincipalCount { get; init; }

    public int EnabledPrincipalCount { get; init; }

    /// <summary>Why the stored document was rejected at startup, when it was. Nothing is recognised
    /// while this is set, so it explains a hub that refuses every key at once.</summary>
    public string? LoadError { get; init; }

    /// <summary>Why the last reload of a mounted document was rejected, when it was. The previous
    /// policy is still in force, which is why this is worth reporting rather than hiding.</summary>
    public string? ReloadError { get; init; }
}

/// <summary>One principal, without anything that could be used to authenticate as it.</summary>
public sealed record PermissionsPrincipalSummary
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public string[] Tools { get; init; } = [];

    /// <summary>The first few characters of the key's hash — enough to tell two keys apart in a
    /// conversation, and no use for authenticating.</summary>
    public string KeyFingerprint { get; init; } = string.Empty;
}

public sealed record PermissionsPrincipalList
{
    public PermissionsPrincipalSummary[] Principals { get; init; } = [];
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
/// Why a tool is or is not available to a principal.
///
/// <para><see cref="Denials"/> holds <em>every</em> reason rather than the first, which is the point
/// of the tool: a grant and a feature switch can both be withholding the same tool, and an operator
/// who fixes one and sees no change will conclude the fix did not work.</para>
/// </summary>
public sealed record PermissionsExplanation
{
    public PermissionsPrincipalSummary Principal { get; init; } = new();

    public string Tool { get; init; } = string.Empty;

    public string ServerKey { get; init; } = string.Empty;

    public bool Available { get; init; }

    public PermissionsDenial[] Denials { get; init; } = [];
}

/// <summary>A newly issued key. The only place one ever appears.</summary>
public sealed record PermissionsKeyIssued
{
    public PermissionsPrincipalSummary Principal { get; init; } = new();

    public string Key { get; init; } = string.Empty;

    /// <summary>Said out loud because the consequence is unrecoverable rather than merely annoying.</summary>
    public string Notice { get; init; } = string.Empty;
}

public sealed record PermissionsChange
{
    public string Message { get; init; } = string.Empty;

    /// <summary>The principal as it now stands, or null when it was deleted or the change was not
    /// about one.</summary>
    public PermissionsPrincipalSummary? Principal { get; init; }
}

/// <summary>Source-generated JSON context for permissions tool results: indented, camelCase, nulls
/// omitted — the same shape <c>ManagementJsonContext</c> uses, so a client parsing one hub surface
/// does not have to switch conventions for another.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PermissionsStatus))]
[JsonSerializable(typeof(PermissionsPrincipalList))]
[JsonSerializable(typeof(PermissionsExplanation))]
[JsonSerializable(typeof(PermissionsKeyIssued))]
[JsonSerializable(typeof(PermissionsChange))]
[JsonSerializable(typeof(PermissionsDenial))]
public sealed partial class PermissionsResultsJsonContext : JsonSerializerContext;
