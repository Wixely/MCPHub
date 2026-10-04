namespace MCPHub.Core.Permissions;

/// <summary>
/// Runtime boundary shared by desktop storage and read-only deployment configuration — the same
/// split <see cref="Routing.IRouterConfigurationSource"/> makes, for the same reason: the enforcement
/// point should not care whether an operator is editing policy in a window or mounting it into a
/// container.
/// </summary>
public interface IPermissionsConfigurationSource
{
    PermissionsConfiguration Snapshot { get; }

    /// <summary>
    /// The principal holding <paramref name="key"/>, or null when no enabled principal does.
    ///
    /// <para>On the source rather than on the caller because the comparison must be constant-time
    /// and the hashing must match what was stored. A consumer handed the snapshot could get both
    /// subtly wrong, and the failure would be silent in the direction that matters.</para>
    /// </summary>
    PermissionsPrincipal? Resolve(string key);
}

/// <summary>
/// A fixed document, for tests and for a host that composes policy in code rather than reading it.
/// </summary>
public sealed class StaticPermissionsSource : IPermissionsConfigurationSource
{
    private readonly PermissionsConfiguration _configuration;

    public StaticPermissionsSource(PermissionsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        PermissionsConfigurationRules.Validate(configuration);
        _configuration = configuration;
    }

    public PermissionsConfiguration Snapshot =>
        _configuration with { Principals = [.. _configuration.Principals] };

    public PermissionsPrincipal? Resolve(string key) =>
        PermissionsResolver.Resolve(_configuration, key);
}

/// <summary>Key comparison, in one place so every source does it identically.</summary>
public static class PermissionsResolver
{
    /// <summary>
    /// The enabled principal whose stored hash matches <paramref name="key"/>.
    ///
    /// <para>Fixed-time comparison per candidate, so the time taken does not reveal how much of a
    /// hash was right. Disabled principals are skipped rather than matched-then-rejected, which
    /// keeps "suspended" indistinguishable from "unknown" to whoever is presenting the key.</para>
    /// </summary>
    public static PermissionsPrincipal? Resolve(PermissionsConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var presented = Convert.FromHexString(PermissionsConfigurationRules.HashKey(key));
        PermissionsPrincipal? found = null;
        foreach (var principal in configuration.Principals)
        {
            if (!principal.Enabled || principal.KeyHash.Length != 64)
            {
                continue;
            }

            // No early exit: every candidate is compared whether or not one has already matched, so
            // the work done is the same for a key that matches the first principal and the last.
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    presented, Convert.FromHexString(principal.KeyHash)))
            {
                found ??= principal;
            }
        }

        return found;
    }
}
