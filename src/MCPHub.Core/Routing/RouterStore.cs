using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCPHub.Core.Infrastructure;

namespace MCPHub.Core.Routing;

/// <summary>Atomic router configuration snapshots. Input keys are hashed, output keys protected at rest.</summary>
public sealed class RouterStore : IRouterConfigurationSource
{
    private readonly object _gate = new();
    private readonly string _path;
    private RouterConfiguration _current;

    public RouterStore(IAppPaths paths)
    {
        _path = Path.Combine(paths.SettingsDirectory, "router.json");
        try
        {
            _current = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), RouterJsonContext.Default.RouterConfiguration)
                    ?? throw new InvalidDataException("Router configuration is empty.")
                : new();
            RouterConfigurationRules.Validate(_current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Keep the rest of MCPHub usable, but never overwrite unreadable configuration.
            LoadError = "Router configuration could not be loaded. Repair router.json and restart MCPHub.";
            _current = new();
        }
    }

    public string? LoadError { get; }
    public RouterConfiguration Snapshot
    {
        get { lock (_gate) return _current with { Inputs = [.. _current.Inputs], Outputs = [.. _current.Outputs] }; }
    }

    public void Configure(int port, bool startOnLaunch) => Update(c => c with { Port = port, StartOnLaunch = startOnLaunch });
    public void SetDefault(string? outputId) => Update(c => c with { DefaultOutputId = EmptyToNull(outputId) });

    /// <summary>Null apiKey retains the stored key; empty apiKey explicitly clears it.</summary>
    public string SaveOutput(string? id, string name, string baseUrl, string? model, string? apiKey)
    {
        id ??= Guid.NewGuid().ToString("N");
        Update(c =>
        {
            var previous = c.Outputs.FirstOrDefault(o => o.Id == id);
            var output = new RouterOutput
            {
                Id = id, Name = name.Trim(), BaseUrl = NormalizeBaseUrl(baseUrl), Model = EmptyToNull(model),
                ProtectedApiKey = apiKey is null ? previous?.ProtectedApiKey : ProtectKey(apiKey),
            };
            return c with { Outputs = [.. c.Outputs.Where(o => o.Id != id), output] };
        });
        return id;
    }

    public void RemoveOutput(string id) => Update(c =>
    {
        if (c.DefaultOutputId == id || c.Inputs.Any(i => i.OutputId == id))
            throw new ArgumentException("Change routes using this output before removing it.");
        return c with { Outputs = c.Outputs.Where(o => o.Id != id).ToArray() };
    });

    public (string Id, string Key) AddInput(string name, string? outputId)
    {
        var key = NewKey();
        var input = new RouterInput { Name = name.Trim(), OutputId = EmptyToNull(outputId), KeyHash = HashKey(key) };
        Update(c => c with { Inputs = [.. c.Inputs, input] });
        return (input.Id, key);
    }

    public void SaveInput(string id, string name, string? outputId, bool enabled) => Update(c =>
    {
        if (!c.Inputs.Any(i => i.Id == id)) throw new ArgumentException("Input no longer exists.");
        return c with { Inputs = c.Inputs.Select(i => i.Id == id ? i with { Name = name.Trim(), OutputId = EmptyToNull(outputId), Enabled = enabled } : i).ToArray() };
    });

    public string RotateKey(string id)
    {
        var key = NewKey();
        Update(c =>
        {
            if (!c.Inputs.Any(i => i.Id == id)) throw new ArgumentException("Input no longer exists.");
            return c with { Inputs = c.Inputs.Select(i => i.Id == id ? i with { KeyHash = HashKey(key) } : i).ToArray() };
        });
        return key;
    }

    public void RemoveInput(string id) => Update(c => c with { Inputs = c.Inputs.Where(i => i.Id != id).ToArray() });

    public RouterRoute? Resolve(string key)
    {
        if (LoadError is not null || key.Length is < 16 or > 256) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        lock (_gate)
        {
            var input = _current.Inputs.FirstOrDefault(i => i.Enabled &&
                CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(i.KeyHash)));
            if (input is null) return null;
            var outputId = input.OutputId ?? _current.DefaultOutputId;
            var output = _current.Outputs.FirstOrDefault(o => o.Id == outputId);
            return new(input.Id, output) { ReadApiKey = () => output is null ? null : ReadApiKey(output) };
        }
    }

    public static string? ReadApiKey(RouterOutput output)
    {
        if (output.ProtectedApiKey is null) return null;
        var parts = output.ProtectedApiKey.Split(':', 2);
        if (parts.Length != 2) throw new CryptographicException("Unrecognized router credential format.");
        var bytes = Convert.FromBase64String(parts[1]);
        return parts[0] switch
        {
            "dpapi" when OperatingSystem.IsWindows() => Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)),
            "userfile" when !OperatingSystem.IsWindows() => Encoding.UTF8.GetString(bytes),
            _ => throw new CryptographicException("Router credential belongs to another platform or user."),
        };
    }

    private void Update(Func<RouterConfiguration, RouterConfiguration> change)
    {
        lock (_gate)
        {
            if (LoadError is not null) throw new InvalidOperationException(LoadError);
            var next = change(_current);
            RouterConfigurationRules.Validate(next);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temp, options))
                {
                    JsonSerializer.Serialize(file, next, RouterJsonContext.Default.RouterConfiguration);
                    file.Flush(flushToDisk: true);
                }
                File.Move(temp, _path, overwrite: true);
                _current = next;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public static string NormalizeBaseUrl(string value) => RouterConfigurationRules.NormalizeBaseUrl(value);

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NewKey() => "mhrouter_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    private static string? ProtectKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key.Length > 4096 || key.Any(char.IsControl)) throw new ArgumentException("Invalid upstream API key.");
        var bytes = Encoding.UTF8.GetBytes(key.Trim());
        return OperatingSystem.IsWindows()
            ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser))
            : "userfile:" + Convert.ToBase64String(bytes);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RouterConfiguration))]
internal sealed partial class RouterJsonContext : JsonSerializerContext;
