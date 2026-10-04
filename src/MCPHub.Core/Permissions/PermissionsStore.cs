using System.Text.Json;
using MCPHub.Core.Infrastructure;

namespace MCPHub.Core.Permissions;

/// <summary>
/// A permissions document that can be changed: the desktop's own policy, and the one a headless host
/// mutates when an operator edits it through the management tools.
///
/// <para>Separate from <see cref="PermissionsDeploymentSource"/> by design, the same way
/// <see cref="Routing.RouterStore"/> is separate from the router's deployment source. A deployment
/// whose policy is a mounted file wants that file to be the only truth — editable policy and
/// read-only policy are different deployments, not two modes of one. A management caller that tries
/// to edit a read-only policy should be told exactly that rather than have its change vanish.</para>
/// </summary>
public interface IWritablePermissions : IPermissionsConfigurationSource
{
    /// <summary>See <see cref="PermissionsConfiguration.AllowUnauthenticated"/>.</summary>
    void SetAllowUnauthenticated(bool allowed);

    /// <summary>
    /// Adds a principal and returns its key, which is <b>the only time the key exists</b> — only its
    /// hash is kept, so a lost key is rotated rather than recovered.
    /// </summary>
    (PermissionsPrincipal Principal, string Key) CreatePrincipal(string name, IReadOnlyList<string> tools);

    /// <summary>Issues a new key for an existing principal, invalidating the old one. Returned once.</summary>
    string RotateKey(string id);

    void SetEnabled(string id, bool enabled);

    /// <summary>Replaces the principal's grants wholesale.</summary>
    void SetGrants(string id, IReadOnlyList<string> tools);

    void DeletePrincipal(string id);
}

/// <summary>
/// <see cref="IWritablePermissions"/> over <c>permissions.json</c> in the settings directory.
///
/// <para>Written the way the router writes its configuration: validated before anything touches the
/// disk, serialised to a temporary file that is then moved over the old one, and created user-only on
/// Unix. The file holds key <em>hashes</em> rather than keys, but a readable list of who may use what
/// is worth protecting on its own.</para>
/// </summary>
public sealed class PermissionsStore : IWritablePermissions
{
    private readonly string _path;
    private readonly object _gate = new();
    private PermissionsConfiguration _current;

    public PermissionsStore(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Combine(paths.SettingsDirectory, "permissions.json");
        try
        {
            _current = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), PermissionsJsonContext.Default.PermissionsConfiguration)
                  ?? new PermissionsConfiguration()
                : new PermissionsConfiguration();
            PermissionsConfigurationRules.Validate(_current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentException or FormatException)
        {
            // Start empty rather than throwing, and say so: a hub that will not launch because one file
            // is corrupt is worse than one that launches recognising nobody and reporting why. An empty
            // document grants nothing, so failing this way cannot widen access.
            _current = new PermissionsConfiguration();
            LoadError = "Permissions could not be loaded. Repair permissions.json and restart MCPHub; "
                        + "until then no principal is recognised.";
        }
    }

    /// <summary>Why the stored document was rejected at startup, or null. Mutating while this is set
    /// throws rather than quietly replacing a file somebody may still want to repair.</summary>
    public string? LoadError { get; }

    public PermissionsConfiguration Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _current with { Principals = [.. _current.Principals] };
            }
        }
    }

    public PermissionsPrincipal? Resolve(string key) => PermissionsResolver.Resolve(Snapshot, key);

    public void SetAllowUnauthenticated(bool allowed) =>
        Update(c => c with { AllowUnauthenticated = allowed });

    public (PermissionsPrincipal Principal, string Key) CreatePrincipal(string name, IReadOnlyList<string> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var key = PermissionsConfigurationRules.NewKey();
        var principal = new PermissionsPrincipal
        {
            Name = name ?? string.Empty,
            KeyHash = PermissionsConfigurationRules.HashKey(key),
            Tools = [.. tools],
        };

        Update(c => c with { Principals = [.. c.Principals, principal] });
        return (principal, key);
    }

    public string RotateKey(string id)
    {
        var key = PermissionsConfigurationRules.NewKey();
        var hash = PermissionsConfigurationRules.HashKey(key);
        Update(c => c with { Principals = [.. Replace(c, id, p => p with { KeyHash = hash })] });
        return key;
    }

    public void SetEnabled(string id, bool enabled) =>
        Update(c => c with { Principals = [.. Replace(c, id, p => p with { Enabled = enabled })] });

    public void SetGrants(string id, IReadOnlyList<string> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Update(c => c with { Principals = [.. Replace(c, id, p => p with { Tools = [.. tools] })] });
    }

    public void DeletePrincipal(string id) =>
        Update(c =>
        {
            var remaining = c.Principals.Where(p => !string.Equals(p.Id, id, StringComparison.Ordinal)).ToArray();
            if (remaining.Length == c.Principals.Length)
            {
                throw new PermissionsNotFoundException(id);
            }

            return c with { Principals = remaining };
        });

    /// <summary>Rebuilds the list with one principal changed, refusing an id that is not there rather
    /// than silently doing nothing — a management call that reports success having changed nothing is
    /// the hardest kind of bug to see.</summary>
    private static List<PermissionsPrincipal> Replace(
        PermissionsConfiguration configuration, string id, Func<PermissionsPrincipal, PermissionsPrincipal> change)
    {
        var next = new List<PermissionsPrincipal>(configuration.Principals.Length);
        var found = false;
        foreach (var principal in configuration.Principals)
        {
            if (string.Equals(principal.Id, id, StringComparison.Ordinal))
            {
                found = true;
                next.Add(change(principal));
            }
            else
            {
                next.Add(principal);
            }
        }

        return found ? next : throw new PermissionsNotFoundException(id);
    }

    private void Update(Func<PermissionsConfiguration, PermissionsConfiguration> change)
    {
        lock (_gate)
        {
            if (LoadError is not null)
            {
                throw new InvalidOperationException(LoadError);
            }

            var next = change(_current);
            PermissionsConfigurationRules.Validate(next);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                };

                // User-only where the platform has the concept. The file is hashes rather than keys,
                // but a list of who may reach which tools is worth keeping to the account that owns it.
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var file = new FileStream(temp, options))
                {
                    JsonSerializer.Serialize(file, next, PermissionsJsonContext.Default.PermissionsConfiguration);
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

/// <summary>No principal has that id. Surfaces to a management caller as a named error, not a crash.</summary>
public sealed class PermissionsNotFoundException(string id)
    : Exception($"No principal has the id '{id}'.")
{
    public string Id { get; } = id;
}
