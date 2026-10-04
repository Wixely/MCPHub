using System.Text.Json;
using System.Text.Json.Serialization;
using MCPHub.Core.Users;

namespace MCPHub.Core.Routing;

/// <summary>
/// Moves identity out of <c>router.json</c> and into the user directory, once.
///
/// <para>The Router used to keep its own callers with their own keys, so an agent that wanted a model
/// route and a tool held two credentials and could be suspended in one place while working in the
/// other. Routes now name a user instead — and this is what makes that change cost nobody their key:
/// a legacy input's id becomes the user's id and its hash comes across untouched, so the key an agent
/// already holds keeps working on the Router and starts working on the proxy.</para>
///
/// <para>Runs when the stored document still carries identity, which is detectable without a flag: a
/// migrated input names a user and a legacy one does not.</para>
/// </summary>
public static class RouterUserMigration
{
    /// <summary>Whether <paramref name="configuration"/> was written before routes named users.</summary>
    public static bool IsNeeded(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.Inputs.Any(i => string.IsNullOrEmpty(i.UserId));
    }

    /// <summary>
    /// Reads the legacy identity out of the document at <paramref name="path"/>, adopts each input as a
    /// user, and returns the routes that replace them.
    ///
    /// <para>An input whose key the directory already holds is matched to that user rather than adopted
    /// twice — importing the same archive on a machine that has already migrated must not mint a second
    /// identity for one key. An input whose id is taken by an <em>unrelated</em> user is adopted under a
    /// fresh id, so both keys keep working rather than one quietly displacing the other.</para>
    /// </summary>
    public static RouterInput[] Migrate(string path, IWritableUsers users)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(users);

        var legacy = JsonSerializer.Deserialize(
                         File.ReadAllText(path), RouterLegacyJsonContext.Default.RouterLegacyConfiguration)
                     ?? throw new ArgumentException("Router configuration is empty.");

        return Migrate(legacy.Inputs ?? [], users);
    }

    /// <inheritdoc cref="Migrate(string, IWritableUsers)"/>
    public static RouterInput[] Migrate(IReadOnlyList<RouterLegacyInput> inputs, IWritableUsers users)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(users);

        var routes = new List<RouterInput>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.KeyHash))
            {
                // Nothing to carry over and nothing that could still authenticate: dropped rather than
                // turned into a route nobody can use.
                continue;
            }

            routes.Add(new RouterInput { UserId = Adopt(input, users), OutputId = input.OutputId });
        }

        return [.. routes];
    }

    private static string Adopt(RouterLegacyInput input, IWritableUsers users)
    {
        var existing = users.Snapshot.Users;

        if (existing.FirstOrDefault(
                u => string.Equals(u.KeyHash, input.KeyHash, StringComparison.OrdinalIgnoreCase)) is { } sameKey)
        {
            return sameKey.Id;
        }

        var taken = existing.Any(u => string.Equals(u.Id, input.Id, StringComparison.Ordinal));
        var user = new HubUser
        {
            // Keeping the legacy id is what lets the activity log and anything else keyed to an input
            // carry on pointing at the same caller.
            Id = taken || string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N") : input.Id,
            Name = string.IsNullOrWhiteSpace(input.Name) ? "Router agent" : input.Name.Trim(),
            Enabled = input.Enabled,
            KeyHash = input.KeyHash!.ToLowerInvariant(),
        };

        users.Adopt(user);
        return user.Id;
    }
}

/// <summary>
/// An input as documents written before the user directory carried it: identity and route together.
/// Read for migration and never written.
/// </summary>
public sealed record RouterLegacyInput
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? KeyHash { get; init; }
    public bool Enabled { get; init; } = true;
    public string? OutputId { get; init; }
}

/// <summary>Just enough of a legacy document to migrate it; everything else is read as usual.</summary>
public sealed record RouterLegacyConfiguration
{
    public RouterLegacyInput[]? Inputs { get; init; }
}

[JsonSerializable(typeof(RouterLegacyConfiguration))]
internal sealed partial class RouterLegacyJsonContext : JsonSerializerContext;
