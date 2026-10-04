namespace MCPHub.Core.Users;

/// <summary>
/// Reading the hub's users. The boundary shared by desktop storage and read-only deployment
/// configuration — the same split the Router and permissions make, for the same reason: whoever
/// authenticates a caller should not care whether an operator edits identity in a window or mounts it
/// into a container.
/// </summary>
public interface IUserDirectory
{
    HubUsersConfiguration Snapshot { get; }

    /// <summary>
    /// The enabled user holding <paramref name="key"/>, or null.
    ///
    /// <para>On the directory rather than on callers because the comparison must be constant-time and
    /// the hashing must match what was stored. Two surfaces getting that subtly different is how one
    /// of them ends up accepting a key the other refuses.</para>
    /// </summary>
    HubUser? Resolve(string key);
}

/// <summary>Changing them. Separate from <see cref="IUserDirectory"/> so a deployment whose identity
/// is a mounted file can refuse edits by name rather than appear to accept them.</summary>
public interface IWritableUsers : IUserDirectory
{
    /// <summary>
    /// Adds a user and returns its key, which is <b>the only time the key exists</b> — only its hash
    /// is kept, so a lost key is rotated rather than recovered.
    /// </summary>
    (HubUser User, string Key) Create(string name);

    /// <summary>
    /// Adds a user whose key was issued elsewhere, keeping the id and hash it arrives with so that key
    /// keeps working.
    ///
    /// <para>For migration and import only — the Router issued its own keys before this directory
    /// existed, and a caller's key must survive being moved here or the migration costs every agent its
    /// credential. Everything else goes through <see cref="Create"/>, where the key is generated and
    /// never supplied.</para>
    /// </summary>
    void Adopt(HubUser user);

    /// <summary>Issues a new key, retiring the old one at once. Returned once.</summary>
    string RotateKey(string id);

    void Rename(string id, string name);

    /// <summary>Suspends or restores the user on every surface at once.</summary>
    void SetEnabled(string id, bool enabled);

    /// <summary>
    /// Removes the user. Whatever is keyed to it — a Router route, a set of tool grants — is left to
    /// the layer that owns it, which drops its own entry rather than having identity reach across. See
    /// <see cref="IUserDependent"/>.
    /// </summary>
    void Delete(string id);
}

/// <summary>
/// Something keyed to a user that has to drop its own entry when that user goes.
///
/// <para>Deleting a user must leave nothing behind: a tool grant or a Router route naming an id nobody
/// holds is at best confusing and at worst inherited by whoever is created next. Each layer implements
/// this for itself rather than identity reaching into it — the user directory does not know what a
/// grant or a route is, and should not have to.</para>
/// </summary>
public interface IUserDependent
{
    /// <summary>Drops everything this layer keeps for that user. Must be harmless for a user it has
    /// nothing for, since deletion calls every implementation.</summary>
    void ForgetUser(string userId);
}

/// <summary>A fixed set, for tests and for a host that composes identity in code.</summary>
public sealed class StaticUserDirectory : IUserDirectory
{
    private readonly HubUsersConfiguration _configuration;

    public StaticUserDirectory(HubUsersConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        UserDirectoryRules.Validate(configuration);
        _configuration = configuration;
    }

    public HubUsersConfiguration Snapshot => _configuration with { Users = [.. _configuration.Users] };

    public HubUser? Resolve(string key) => UserKeys.Resolve(_configuration, key);
}

/// <summary>No user has that id. Surfaces to a management caller as a named error, not a crash.</summary>
public sealed class UserNotFoundException(string id) : Exception($"No user has the id '{id}'.")
{
    public string Id { get; } = id;
}
