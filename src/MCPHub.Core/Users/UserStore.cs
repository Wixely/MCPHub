using System.Text.Json;
using MCPHub.Core.Infrastructure;

namespace MCPHub.Core.Users;

/// <summary>
/// <see cref="IWritableUsers"/> over <c>users.json</c> in the settings directory.
///
/// <para>Written the way the Router writes its configuration: validated before anything touches the
/// disk, serialised to a temporary file that is then moved over the old one, and created user-only on
/// Unix. The file holds key hashes rather than keys, but the list of who may reach this hub is worth
/// keeping to the account that owns it.</para>
/// </summary>
public sealed class UserStore : IWritableUsers
{
    private readonly string _path;
    private readonly object _gate = new();
    private HubUsersConfiguration _current;

    public UserStore(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Combine(paths.SettingsDirectory, "users.json");
        try
        {
            var stored = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), UsersJsonContext.Default.HubUsersConfiguration)
                  ?? new HubUsersConfiguration()
                : new HubUsersConfiguration();

            // See UserDirectoryRules.Validate: an absent member arrives null, so it is filled in here and
            // the rest of the hub only ever sees a list.
            _current = stored with { Users = stored.Users ?? [] };
            UserDirectoryRules.Validate(_current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentException or FormatException)
        {
            // Start empty rather than throwing, and say so. A hub that will not launch because one file
            // is corrupt is worse than one that launches recognising nobody and reports why — and an
            // empty directory authenticates nobody, so failing this way cannot admit anyone.
            _current = new HubUsersConfiguration();
            LoadError = "Users could not be loaded. Repair users.json and restart MCPHub; "
                        + "until then no key is recognised.";
        }
    }

    /// <summary>Why the stored document was rejected at startup, or null. Editing while this is set
    /// throws rather than quietly replacing a file somebody may still want to repair.</summary>
    public string? LoadError { get; }

    public HubUsersConfiguration Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _current with { Users = [.. _current.Users] };
            }
        }
    }

    public HubUser? Resolve(string key) => UserKeys.Resolve(Snapshot, key);

    public (HubUser User, string Key) Create(string name)
    {
        var key = UserKeys.New();
        var user = new HubUser { Name = name?.Trim() ?? string.Empty, KeyHash = UserKeys.Hash(key) };
        Update(c => c with { Users = [.. c.Users, user] });
        return (user, key);
    }

    public void Adopt(HubUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        // Validation is the Update path's, so an id or a hash already in the directory is refused here
        // rather than producing a second entry that resolution would pick between arbitrarily.
        Update(c => c with { Users = [.. c.Users, user with { Name = user.Name?.Trim() ?? string.Empty }] });
    }

    public string RotateKey(string id)
    {
        var key = UserKeys.New();
        var hash = UserKeys.Hash(key);
        Update(c => c with { Users = [.. Replace(c, id, u => u with { KeyHash = hash })] });
        return key;
    }

    public void Rename(string id, string name) =>
        Update(c => c with { Users = [.. Replace(c, id, u => u with { Name = name?.Trim() ?? string.Empty })] });

    public void SetEnabled(string id, bool enabled) =>
        Update(c => c with { Users = [.. Replace(c, id, u => u with { Enabled = enabled })] });

    public void Delete(string id) =>
        Update(c =>
        {
            var remaining = c.Users.Where(u => !string.Equals(u.Id, id, StringComparison.Ordinal)).ToArray();
            return remaining.Length == c.Users.Length ? throw new UserNotFoundException(id) : c with { Users = remaining };
        });

    /// <summary>
    /// Folds an imported set into the stored one and reports what changed.
    ///
    /// <para>Merged rather than replaced, and the asymmetry is deliberate: a user the archive carries is
    /// taken as written, because the archive is where the routes and grants referring to it came from,
    /// but a local user the archive has never heard of is left alone. Replacing wholesale would retire
    /// the key of every caller that happened not to be on the machine the archive came from.</para>
    /// </summary>
    public (int Added, int Updated) Merge(IReadOnlyList<HubUser> users)
    {
        ArgumentNullException.ThrowIfNull(users);

        var added = 0;
        var updated = 0;
        Update(c =>
        {
            added = 0;
            updated = 0;
            var next = c.Users.ToList();
            foreach (var incoming in users)
            {
                if (incoming is null || string.IsNullOrWhiteSpace(incoming.Id)) continue;
                var at = next.FindIndex(u => string.Equals(u.Id, incoming.Id, StringComparison.Ordinal));
                if (at >= 0)
                {
                    next[at] = incoming;
                    updated++;
                }
                else
                {
                    next.Add(incoming);
                    added++;
                }
            }

            return c with { Users = [.. next] };
        });

        return (added, updated);
    }

    /// <summary>Rebuilds the list with one user changed, refusing an id that is not there rather than
    /// silently doing nothing — a management call that reports success having changed nothing is the
    /// hardest kind of bug to see.</summary>
    private static List<HubUser> Replace(
        HubUsersConfiguration configuration, string id, Func<HubUser, HubUser> change)
    {
        var next = new List<HubUser>(configuration.Users.Length);
        var found = false;
        foreach (var user in configuration.Users)
        {
            if (string.Equals(user.Id, id, StringComparison.Ordinal))
            {
                found = true;
                next.Add(change(user));
            }
            else
            {
                next.Add(user);
            }
        }

        return found ? next : throw new UserNotFoundException(id);
    }

    private void Update(Func<HubUsersConfiguration, HubUsersConfiguration> change)
    {
        lock (_gate)
        {
            if (LoadError is not null)
            {
                throw new InvalidOperationException(LoadError);
            }

            var next = change(_current);
            UserDirectoryRules.Validate(next);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var file = new FileStream(temp, options))
                {
                    JsonSerializer.Serialize(file, next, UsersJsonContext.Default.HubUsersConfiguration);
                    file.Flush(flushToDisk: true);
                }

                File.Move(temp, _path, overwrite: true);
                _current = next;
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
    }
}
