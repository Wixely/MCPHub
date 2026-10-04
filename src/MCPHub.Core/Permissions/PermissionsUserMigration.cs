using System.Text.Json;
using System.Text.Json.Serialization;
using MCPHub.Core.Users;

namespace MCPHub.Core.Permissions;

/// <summary>
/// Moves the permissions document's own principals into the user directory, once.
///
/// <para>Permissions used to carry identity: each principal had a name, a key hash and an enabled flag
/// alongside the tools it could use, which is why a caller needed one key here and another on the
/// Router. Grants now name a user — and this is what stops that change costing anybody a key: a
/// principal's id becomes the user's id and its hash comes across untouched, so the key an agent
/// already holds keeps working.</para>
///
/// <para>The sibling of <see cref="Routing.RouterUserMigration"/>, and deliberately the same shape.</para>
/// </summary>
public static class PermissionsUserMigration
{
    /// <summary>
    /// Reads the legacy principals out of the document at <paramref name="path"/>, adopts each as a
    /// user, and returns the grants that replace them. Returns empty when there is nothing to migrate.
    ///
    /// <para>A principal whose key the directory already holds is matched to that user rather than
    /// adopted twice; one whose id is taken by an unrelated user is adopted under a fresh id, so both
    /// keys keep working rather than one quietly displacing the other.</para>
    /// </summary>
    public static PermissionsGrant[] Migrate(string path, IWritableUsers users)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(users);

        var legacy = JsonSerializer.Deserialize(
            File.ReadAllText(path), PermissionsLegacyJsonContext.Default.PermissionsLegacyConfiguration);

        var grants = new List<PermissionsGrant>();
        foreach (var principal in legacy?.Principals ?? [])
        {
            if (principal is null || string.IsNullOrWhiteSpace(principal.KeyHash))
            {
                // Nothing that could still authenticate anybody: dropped rather than turned into a grant
                // for a user with no key.
                continue;
            }

            grants.Add(new PermissionsGrant
            {
                UserId = Adopt(principal, users),
                Tools = [.. principal.Tools ?? []],
            });
        }

        return [.. grants];
    }

    private static string Adopt(PermissionsLegacyPrincipal principal, IWritableUsers users)
    {
        var existing = users.Snapshot.Users;

        if (existing.FirstOrDefault(
                u => string.Equals(u.KeyHash, principal.KeyHash, StringComparison.OrdinalIgnoreCase)) is { } sameKey)
        {
            return sameKey.Id;
        }

        var taken = existing.Any(u => string.Equals(u.Id, principal.Id, StringComparison.Ordinal));
        var user = new HubUser
        {
            Id = taken || string.IsNullOrWhiteSpace(principal.Id) ? Guid.NewGuid().ToString("N") : principal.Id,
            Name = string.IsNullOrWhiteSpace(principal.Name) ? "Agent" : principal.Name.Trim(),
            Enabled = principal.Enabled,
            KeyHash = principal.KeyHash!.ToLowerInvariant(),
        };

        users.Adopt(user);
        return user.Id;
    }
}

/// <summary>A principal as documents written before the user directory carried it: identity and grants
/// together. Read for migration and never written.</summary>
public sealed record PermissionsLegacyPrincipal
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? KeyHash { get; init; }
    public bool Enabled { get; init; } = true;
    public string[]? Tools { get; init; }
}

/// <summary>Just enough of a legacy document to migrate it.</summary>
public sealed record PermissionsLegacyConfiguration
{
    public PermissionsLegacyPrincipal[]? Principals { get; init; }
}

[JsonSerializable(typeof(PermissionsLegacyConfiguration))]
internal sealed partial class PermissionsLegacyJsonContext : JsonSerializerContext;
