using System.Text.Json.Serialization;

namespace MCPHub.Core.Users;

/// <summary>One user, without anything that could be used to authenticate as it.</summary>
public sealed record UserSummary
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    /// <summary>The first characters of the key's hash — enough to tell two keys apart in a support
    /// conversation, and no use for authenticating.</summary>
    public string KeyFingerprint { get; init; } = string.Empty;
}

public sealed record UserList
{
    public UserSummary[] Users { get; init; } = [];

    /// <summary>False when identity is mounted read-only, so a client can grey its editing controls
    /// rather than let somebody make changes that cannot land.</summary>
    public bool Editable { get; init; }

    /// <summary>Why the stored directory was rejected at startup, when it was. No key is recognised
    /// while this is set, which explains a hub refusing every caller at once.</summary>
    public string? LoadError { get; init; }
}

/// <summary>A newly issued key. The only place one ever appears.</summary>
public sealed record UserKeyIssued
{
    public UserSummary User { get; init; } = new();

    public string Key { get; init; } = string.Empty;

    /// <summary>Said out loud because the consequence is unrecoverable rather than merely annoying.</summary>
    public string Notice { get; init; } = string.Empty;
}

public sealed record UserChange
{
    public string Message { get; init; } = string.Empty;

    /// <summary>The user as it now stands, or null when it was deleted.</summary>
    public UserSummary? User { get; init; }
}

/// <summary>A failure a caller can act on, with a stable code to branch on.</summary>
public sealed record UserError
{
    public string Code { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? Remedy { get; init; }
}

/// <summary>Indented, camelCase, nulls omitted — the convention every other tool surface here uses.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserList))]
[JsonSerializable(typeof(UserKeyIssued))]
[JsonSerializable(typeof(UserChange))]
[JsonSerializable(typeof(UserError))]
public sealed partial class UserResultsJsonContext : JsonSerializerContext;
