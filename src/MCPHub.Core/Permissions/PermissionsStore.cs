using System.Text.Json;
using MCPHub.Core.Infrastructure;

namespace MCPHub.Core.Permissions;

/// <summary>
/// <see cref="IWritablePermissions"/> over <c>permissions.json</c> in the settings directory.
///
/// <para>Grants only — keys and names live in <c>users.json</c>. Written the way the Router writes its
/// configuration: validated before anything touches the disk, serialised to a temporary file that is
/// then moved over the old one, and created user-only on Unix.</para>
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
            // is corrupt is worse than one that launches granting nothing and reports why. An empty
            // document grants nothing, so failing this way cannot widen access.
            _current = new PermissionsConfiguration();
            LoadError = "Permissions could not be loaded. Repair permissions.json and restart MCPHub; "
                        + "until then no user is granted any tool.";
        }
    }

    /// <summary>Why the stored document was rejected at startup, or null. Editing while this is set
    /// throws rather than quietly replacing a file somebody may still want to repair.</summary>
    public string? LoadError { get; }

    public PermissionsConfiguration Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _current with { Grants = [.. _current.Grants] };
            }
        }
    }

    public PermissionsGrant? GrantsFor(string userId)
    {
        lock (_gate)
        {
            return _current.Grants.FirstOrDefault(g => string.Equals(g.UserId, userId, StringComparison.Ordinal));
        }
    }

    public void SetAllowUnauthenticated(bool allowed) =>
        Update(c => c with { AllowUnauthenticated = allowed });

    public void SetGrants(string userId, IReadOnlyList<string> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(tools);
        Update(c =>
        {
            var next = c.Grants
                .Where(g => !string.Equals(g.UserId, userId, StringComparison.Ordinal))
                .Append(new PermissionsGrant { UserId = userId, Tools = [.. tools] })
                .ToArray();
            return c with { Grants = next };
        });
    }

    public void ForgetUser(string userId) =>
        Update(c => c with
        {
            Grants = [.. c.Grants.Where(g => !string.Equals(g.UserId, userId, StringComparison.Ordinal))],
        });

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
