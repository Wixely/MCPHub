using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPHub.Core.Permissions;

/// <summary>
/// Read-only, portable permissions for headless hosts. Never reads or writes desktop settings — the
/// same contract <see cref="Routing.RouterDeploymentSource"/> keeps, so a container's policy is a
/// mounted file and nothing else.
///
/// <para>A key may be given three ways: already hashed in the document, or as a secret in a file or
/// an environment variable which this hashes on load. The second and third are what let a compose
/// file carry policy in git and the keys in a secret store, and giving both for one principal is
/// refused rather than silently preferred.</para>
/// </summary>
public sealed class PermissionsDeploymentSource : IPermissionsConfigurationSource
{
    private readonly string _path;
    private readonly Func<string, string?> _environment;
    private PermissionsConfiguration _state;
    private string? _reloadError;

    public PermissionsDeploymentSource(string path, Func<string, string?>? environment = null)
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
            // logs, and a key is exactly the thing an exception message would carry outward.
            throw new InvalidOperationException(
                "Permissions deployment configuration is invalid or a referenced secret is unavailable.");
        }
    }

    public PermissionsConfiguration Snapshot
    {
        get
        {
            var current = Volatile.Read(ref _state);
            return current with { Principals = [.. current.Principals] };
        }
    }

    /// <summary>Why the last <see cref="Reload"/> was rejected, or null. The first load throws
    /// instead: a host with no valid policy should not start, where a running host with a newly
    /// broken file should keep the policy it already had.</summary>
    public string? ReloadError => Volatile.Read(ref _reloadError);

    public PermissionsPrincipal? Resolve(string key) => PermissionsResolver.Resolve(Snapshot, key);

    /// <summary>
    /// Re-read the file, keeping the current policy if the new one cannot be enforced. Returns
    /// whether the reload succeeded, as the router's does — not whether anything changed.
    ///
    /// <para>Atomic by construction: the document is parsed and validated in full before anything is
    /// published, so a half-written file cannot leave half a policy in force — which for an
    /// allow-list would mean granting more than the file says.</para>
    /// </summary>
    public bool Reload()
    {
        try
        {
            var next = Read();
            Volatile.Write(ref _state, next);
            Volatile.Write(ref _reloadError, null);
            return true;
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            Volatile.Write(
                ref _reloadError,
                "Permissions reload rejected. The last valid policy remains in force; check the document and its secrets.");
            return false;
        }
    }

    private PermissionsConfiguration Read()
    {
        var document = JsonSerializer.Deserialize(
                           File.ReadAllText(_path), PermissionsJsonContext.Default.PermissionsDeploymentConfiguration)
                       ?? throw new ArgumentException("Empty permissions document.");

        var principals = document.Principals.Select(p =>
        {
            if (p is null)
            {
                throw new ArgumentException("Invalid principal.");
            }

            var secret = ReadSecret(p.KeyFile, p.KeyEnvironmentVariable, required: p.KeyHash is null);
            if (secret is not null && p.KeyHash is not null)
            {
                throw new ArgumentException("Choose one principal credential source.");
            }

            if (secret is not null
                && secret.Length is < PermissionsConfigurationRules.MinimumKeyLength
                    or > PermissionsConfigurationRules.MaximumKeyLength)
            {
                throw new ArgumentException(
                    $"Principal keys must contain {PermissionsConfigurationRules.MinimumKeyLength}–"
                    + $"{PermissionsConfigurationRules.MaximumKeyLength} characters.");
            }

            return new PermissionsPrincipal
            {
                Id = p.Id,
                Name = p.Name,
                Enabled = p.Enabled,
                Tools = [.. p.Tools],
                KeyHash = p.KeyHash ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret!))),
            };
        }).ToArray();

        var runtime = new PermissionsConfiguration
        {
            SchemaVersion = document.SchemaVersion,
            AllowUnauthenticated = document.AllowUnauthenticated,
            Principals = principals,
        };
        PermissionsConfigurationRules.Validate(runtime);
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

        if (required || file is not null || environmentVariable is not null)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            {
                throw new ArgumentException("Missing or invalid secret.");
            }
        }

        return value;
    }

    private static bool IsConfigurationError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or FormatException or OverflowException;
}

/// <summary>
/// The on-disk shape, which is deliberately not the runtime shape: a deployment names a secret
/// source where the runtime holds only a hash. Keeping them as separate records is what makes it
/// impossible for a hash-only runtime policy to carry a path to a key around with it.
/// </summary>
public sealed record PermissionsDeploymentConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>See <see cref="PermissionsConfiguration.AllowUnauthenticated"/>. A deployment that
    /// issues keys wants this <see langword="false"/>; it defaults <see langword="true"/> only so
    /// that the shape matches the desktop's.</summary>
    public bool AllowUnauthenticated { get; init; } = true;

    public PermissionsDeploymentPrincipal[] Principals { get; init; } = [];
}

/// <summary>One principal as a deployment writes it. Exactly one of <see cref="KeyHash"/>,
/// <see cref="KeyFile"/> or <see cref="KeyEnvironmentVariable"/>.</summary>
public sealed record PermissionsDeploymentPrincipal
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;

    /// <summary>A pre-computed SHA-256, 64 lowercase hex characters — for policy kept in git with no
    /// secret anywhere near it.</summary>
    public string? KeyHash { get; init; }

    /// <summary>A file holding the key, resolved relative to this document. For a mounted secret.</summary>
    public string? KeyFile { get; init; }

    /// <summary>An environment variable holding the key. For a compose file or an orchestrator.</summary>
    public string? KeyEnvironmentVariable { get; init; }

    public string[] Tools { get; init; } = [];
}

/// <summary>
/// The stored documents, written as the router writes its configuration — property names as declared.
/// Tool RESULTS use <see cref="PermissionsResultsJsonContext"/> instead, which is camelCase like the
/// management tools' results: a file a person edits and a payload a client parses are different
/// audiences, and the two siblings already differ this way.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PermissionsConfiguration))]
[JsonSerializable(typeof(PermissionsDeploymentConfiguration))]
internal sealed partial class PermissionsJsonContext : JsonSerializerContext;
