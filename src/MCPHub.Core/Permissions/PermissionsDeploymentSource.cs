using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPHub.Core.Permissions;

/// <summary>
/// Read-only, portable grants for headless hosts. Never reads or writes desktop settings.
///
/// <para>Simpler than the user directory's equivalent, and for a good reason: there are no secrets in
/// here. A grant document names users and tools, so it can sit in source control as it is — the keys
/// it refers to are the directory's business.</para>
/// </summary>
public sealed class PermissionsDeploymentSource : IPermissionsConfigurationSource
{
    private readonly string _path;
    private PermissionsConfiguration _state;
    private string? _reloadError;

    public PermissionsDeploymentSource(string path)
    {
        _path = Path.GetFullPath(path);
        try
        {
            _state = Read();
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            // No payload in the message: a grant document names users and tools, and neither belongs
            // in a log by accident.
            throw new InvalidOperationException("Permissions deployment configuration is invalid.");
        }
    }

    public PermissionsConfiguration Snapshot
    {
        get
        {
            var current = Volatile.Read(ref _state);
            return current with { Grants = [.. current.Grants] };
        }
    }

    /// <summary>Why the last <see cref="Reload"/> was rejected, or null. The first load throws instead:
    /// a host with no valid policy should not start, where a running host with a newly broken file
    /// should keep the policy it had.</summary>
    public string? ReloadError => Volatile.Read(ref _reloadError);

    public PermissionsGrant? GrantsFor(string userId) =>
        Snapshot.Grants.FirstOrDefault(g => string.Equals(g.UserId, userId, StringComparison.Ordinal));

    /// <summary>Re-reads the file, keeping the current policy if the new one cannot be enforced.
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
                "Permissions reload rejected. The last valid policy remains in force; check the document.");
            return false;
        }
    }

    private PermissionsConfiguration Read()
    {
        var document = JsonSerializer.Deserialize(
                           File.ReadAllText(_path), PermissionsJsonContext.Default.PermissionsConfiguration)
                       ?? throw new ArgumentException("Empty permissions document.");

        // An absent member deserialises to null rather than its initialiser, so a document with no
        // grants section grants nothing — which is what it says.
        var normalised = document with { Grants = document.Grants ?? [] };
        PermissionsConfigurationRules.Validate(normalised);
        return normalised;
    }

    private static bool IsConfigurationError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or FormatException or OverflowException;
}

/// <summary>
/// The stored document, written as the Router writes its configuration — property names as declared.
/// Tool RESULTS use <see cref="PermissionsResultsJsonContext"/> instead, which is camelCase like the
/// management tools' results: a file a person edits and a payload a client parses are different
/// audiences.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PermissionsConfiguration))]
internal sealed partial class PermissionsJsonContext : JsonSerializerContext;
