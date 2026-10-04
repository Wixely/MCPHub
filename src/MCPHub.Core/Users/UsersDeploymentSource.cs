using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPHub.Core.Users;

/// <summary>
/// Read-only, portable users for headless hosts. Never reads or writes desktop settings — the same
/// contract the Router's deployment source keeps, so a container's identity is a mounted file and
/// nothing else.
///
/// <para>A key may be given three ways: already hashed in the document, or as a secret in a file or an
/// environment variable which this hashes on load. The second and third are what let a compose file
/// carry identity in git and the keys in a secret store; giving both for one user is refused rather
/// than silently preferred.</para>
/// </summary>
public sealed class UsersDeploymentSource : IUserDirectory
{
    private readonly string _path;
    private readonly Func<string, string?> _environment;
    private HubUsersConfiguration _state;
    private string? _reloadError;

    public UsersDeploymentSource(string path, Func<string, string?>? environment = null)
    {
        _path = Path.GetFullPath(path);
        _environment = environment ?? Environment.GetEnvironmentVariable;
        try
        {
            _state = Read();
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            // No exception payload: JSON values, secret paths and environment contents must not enter
            // logs, and a key is exactly what such a message would carry outward.
            throw new InvalidOperationException(
                "User deployment configuration is invalid or a referenced secret is unavailable.");
        }
    }

    public HubUsersConfiguration Snapshot
    {
        get
        {
            var current = Volatile.Read(ref _state);
            return current with { Users = [.. current.Users] };
        }
    }

    /// <summary>Why the last <see cref="Reload"/> was rejected, or null. The first load throws instead:
    /// a host with no valid identity should not start, where a running host with a newly broken file
    /// should keep the users it already had.</summary>
    public string? ReloadError => Volatile.Read(ref _reloadError);

    public HubUser? Resolve(string key) => UserKeys.Resolve(Snapshot, key);

    /// <summary>Re-reads the file, keeping the current users if the new document cannot be used.
    /// Returns whether the reload succeeded, as the Router's does — not whether anything changed.</summary>
    public bool Reload()
    {
        try
        {
            Volatile.Write(ref _state, Read());
            Volatile.Write(ref _reloadError, null);
            return true;
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            Volatile.Write(
                ref _reloadError,
                "User reload rejected. The last valid users remain in force; check the document and its secrets.");
            return false;
        }
    }

    private HubUsersConfiguration Read()
    {
        var document = JsonSerializer.Deserialize(
                           File.ReadAllText(_path), UsersJsonContext.Default.UsersDeploymentConfiguration)
                       ?? throw new ArgumentException("Empty users document.");

        // An absent member deserialises to null rather than its initialiser, so a document naming no
        // users at all means exactly that.
        var users = (document.Users ?? []).Select(u =>
        {
            if (u is null)
            {
                throw new ArgumentException("Invalid user.");
            }

            var secret = ReadSecret(u.KeyFile, u.KeyEnvironmentVariable, required: u.KeyHash is null);
            if (secret is not null && u.KeyHash is not null)
            {
                throw new ArgumentException("Choose one user credential source.");
            }

            if (secret is not null && secret.Length is < UserKeys.MinimumLength or > UserKeys.MaximumLength)
            {
                throw new ArgumentException(
                    $"User keys must contain {UserKeys.MinimumLength}–{UserKeys.MaximumLength} characters.");
            }

            return new HubUser
            {
                Id = u.Id,
                Name = u.Name,
                Enabled = u.Enabled,
                KeyHash = u.KeyHash ?? UserKeys.Hash(secret!),
            };
        }).ToArray();

        var runtime = new HubUsersConfiguration { SchemaVersion = document.SchemaVersion, Users = users };
        UserDirectoryRules.Validate(runtime);
        return runtime;
    }

    private string? ReadSecret(string? file, string? environmentVariable, bool required)
    {
        if (file is not null && environmentVariable is not null)
        {
            throw new ArgumentException("Choose one secret source.");
        }

        string? value = null;
        if (file is not null)
        {
            var path = Path.GetFullPath(file, Path.GetDirectoryName(_path)!);
            if (new FileInfo(path).Length > 8192)
            {
                throw new InvalidDataException("Secret is too large.");
            }

            value = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        else if (environmentVariable is not null)
        {
            value = _environment(environmentVariable);
        }

        if ((required || file is not null || environmentVariable is not null)
            && (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)))
        {
            throw new ArgumentException("Missing or invalid secret.");
        }

        return value;
    }

    private static bool IsConfigurationError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or FormatException or OverflowException;
}

/// <summary>The on-disk shape, which is deliberately not the runtime shape: a deployment names a secret
/// source where the runtime holds only a hash.</summary>
public sealed record UsersDeploymentConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    public UsersDeploymentUser[] Users { get; init; } = [];
}

/// <summary>One user as a deployment writes it. Exactly one of <see cref="KeyHash"/>,
/// <see cref="KeyFile"/> or <see cref="KeyEnvironmentVariable"/>.</summary>
public sealed record UsersDeploymentUser
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>A pre-computed SHA-256, 64 lowercase hex characters — for identity kept in git with no
    /// secret anywhere near it.</summary>
    public string? KeyHash { get; init; }

    /// <summary>A file holding the key, resolved relative to this document. For a mounted secret.</summary>
    public string? KeyFile { get; init; }

    /// <summary>An environment variable holding the key. For a compose file or an orchestrator.</summary>
    public string? KeyEnvironmentVariable { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HubUsersConfiguration))]
[JsonSerializable(typeof(UsersDeploymentConfiguration))]
internal sealed partial class UsersJsonContext : JsonSerializerContext;
