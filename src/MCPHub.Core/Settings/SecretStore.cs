using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCPHub.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Settings;

/// <summary>Well-known secret keys.</summary>
public static class SecretKeys
{
    public const string GithubPat = "github_pat";
}

/// <summary>Stores small secrets (e.g. the GitHub PAT). Never plaintext on Windows.</summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string? value);
    bool Has(string key);
}

/// <summary>
/// File-backed secret store. On Windows each value is encrypted with DPAPI (current user); on other
/// platforms values are stored base64 in a user-only file with a warning (no OS keychain integration yet).
/// </summary>
public sealed class SecretStore : ISecretStore
{
    private readonly string _path;
    private readonly ILogger<SecretStore> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _entries;

    public SecretStore(IAppPaths appPaths, ILogger<SecretStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appPaths.SettingsDirectory);
        _path = Path.Combine(appPaths.SettingsDirectory, "secrets.json");
        _entries = Load();
    }

    public bool Has(string key)
    {
        lock (_gate)
            return _entries.ContainsKey(key);
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var stored) || string.IsNullOrEmpty(stored))
                return null;

            try
            {
                var bytes = Convert.FromBase64String(stored);
                var plain = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read secret {Key}.", key);
                return null;
            }
        }
    }

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(value))
            {
                _entries.Remove(key);
            }
            else
            {
                var plain = Encoding.UTF8.GetBytes(value);
                var bytes = OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser) : plain;
                _entries[key] = Convert.ToBase64String(bytes);
            }

            Save();
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Could not read secrets; starting empty.");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);

            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch (Exception ex) { _logger.LogDebug(ex, "Could not restrict secrets file mode."); }
                _logger.LogWarning("Secrets are stored without OS encryption on this platform (user-only file).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger.LogError(ex, "Failed to save secrets.");
        }
    }
}
