namespace MCPHub.Core.Permissions;

/// <summary>
/// Reading the grant document. The boundary shared by desktop storage and read-only deployment
/// configuration, as the Router and the user directory both have.
/// </summary>
public interface IPermissionsConfigurationSource
{
    PermissionsConfiguration Snapshot { get; }

    /// <summary>What this user may use, or null when it has no grants at all — which is the default
    /// and means it may use nothing.</summary>
    PermissionsGrant? GrantsFor(string userId);
}

/// <summary>Changing it. Separate so a deployment whose policy is a mounted file can refuse edits by
/// name rather than appear to accept them.</summary>
public interface IWritablePermissions : IPermissionsConfigurationSource, Users.IUserDependent
{
    /// <summary>See <see cref="PermissionsConfiguration.AllowUnauthenticated"/>.</summary>
    void SetAllowUnauthenticated(bool allowed);

    /// <summary>Replaces this user's grants, adding an entry when it had none. An empty list leaves the
    /// user granted nothing.</summary>
    void SetGrants(string userId, IReadOnlyList<string> tools);

    /// <summary>
    /// Forgets a user's grants entirely. <see cref="Users.IUserDependent.ForgetUser"/> as this layer
    /// implements it — called when a user is deleted, and also on its own to tidy an entry left behind.
    /// </summary>
    new void ForgetUser(string userId);
}

/// <summary>A fixed document, for tests and for a host composing policy in code.</summary>
public sealed class StaticPermissionsSource : IPermissionsConfigurationSource
{
    private readonly PermissionsConfiguration _configuration;

    public StaticPermissionsSource(PermissionsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        PermissionsConfigurationRules.Validate(configuration);
        _configuration = configuration with { Grants = configuration.Grants ?? [] };
    }

    public PermissionsConfiguration Snapshot => _configuration with { Grants = [.. _configuration.Grants] };

    public PermissionsGrant? GrantsFor(string userId) =>
        _configuration.Grants.FirstOrDefault(g => string.Equals(g.UserId, userId, StringComparison.Ordinal));
}
