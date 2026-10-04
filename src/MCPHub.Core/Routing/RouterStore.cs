using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Users;

namespace MCPHub.Core.Routing;

/// <summary>Atomic router configuration snapshots. Input keys are hashed, output keys protected at rest.</summary>
public sealed class RouterStore : IRouterConfigurationSource, IUserDependent
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IUserDirectory _users;
    private RouterConfiguration _current;

    /// <param name="users">Who a key belongs to. The Router used to answer that itself, with its own
    /// keys; asking the one directory instead is what makes a caller's key work on both surfaces and one
    /// suspension stop both.</param>
    public RouterStore(IAppPaths paths, IUserDirectory users)
    {
        ArgumentNullException.ThrowIfNull(users);
        _users = users;
        _path = Path.Combine(paths.SettingsDirectory, "router.json");
        try
        {
            _current = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), RouterJsonContext.Default.RouterConfiguration)
                    ?? throw new InvalidDataException("Router configuration is empty.")
                : new();
            // Settings added after a file was written arrive absent, not defaulted. Filling the bind address
            // in here means the rest of the app only ever sees a concrete one, and the next save records it.
            _current = _current with { BindAddress = RouterConfigurationRules.CoerceBindAddress(_current.BindAddress) };
            if (RouterUserMigration.IsNeeded(_current))
            {
                _current = _current with { Inputs = Migrate() };
            }

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

    /// <summary>
    /// Persists the listener settings. A running host keeps its current socket until it is restarted —
    /// see <see cref="RouterHost.ApplyListenerAsync"/>, which saves and rebinds in one step.
    /// </summary>
    public void Configure(string bindAddress, int port, bool startOnLaunch) =>
        Update(c => c with { BindAddress = RouterConfigurationRules.NormalizeBindAddress(bindAddress), Port = port, StartOnLaunch = startOnLaunch });
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

    /// <summary>
    /// Gives a user Router access, or moves the output it routes to. No key is issued: the user already
    /// holds one, and issuing a second here is the mistake this whole arrangement removes.
    /// </summary>
    public void SetRoute(string userId, string? outputId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (_users.Snapshot.Users.All(u => !string.Equals(u.Id, userId, StringComparison.Ordinal)))
            throw new ArgumentException("That user no longer exists. Add it on the Users page first.");
        var input = new RouterInput { UserId = userId, OutputId = EmptyToNull(outputId) };
        Update(c => c with { Inputs = [.. c.Inputs.Where(i => i.UserId != userId), input] });
    }

    /// <summary>
    /// Takes Router access away, leaving the user and its key alone — it may still be reaching tools
    /// through the proxy. Called when a user is deleted too, so no route is left naming nobody.
    /// </summary>
    public void RemoveRoute(string userId) =>
        Update(c => c with { Inputs = c.Inputs.Where(i => i.UserId != userId).ToArray() });

    /// <summary>What this layer keeps for a deleted user: its route, and nothing else. Harmless for a
    /// user that never had one, which matters because deletion calls every implementation.</summary>
    void IUserDependent.ForgetUser(string userId) => RemoveRoute(userId);

    /// <summary>
    /// Replaces the whole routing table from a settings archive. Outputs arrive without credentials, so each
    /// one's key is taken from <paramref name="apiKeys"/> when the archive carried it, and otherwise from the
    /// output already stored under that id — which is what lets an unencrypted archive move a topology onto a
    /// machine that already holds the keys. Listener settings are applied only when
    /// <paramref name="includeListener"/> is set, so importing routes need not move someone else's port.
    /// </summary>
    public void Import(RouterConfiguration incoming, IReadOnlyDictionary<string, string>? apiKeys = null, bool includeListener = true)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        Update(current =>
        {
            var existing = current.Outputs.ToDictionary(o => o.Id, o => o.ProtectedApiKey, StringComparer.Ordinal);
            var outputs = incoming.Outputs.Select(output => output with
            {
                Name = output.Name.Trim(),
                BaseUrl = NormalizeBaseUrl(output.BaseUrl),
                Model = EmptyToNull(output.Model),
                ProtectedApiKey = apiKeys is not null && apiKeys.TryGetValue(output.Id, out var plain) && !string.IsNullOrWhiteSpace(plain)
                    ? ProtectKey(plain)
                    : existing.GetValueOrDefault(output.Id),
            }).ToArray();

            return current with
            {
                BindAddress = includeListener ? RouterConfigurationRules.NormalizeBindAddress(incoming.BindAddress) : current.BindAddress,
                Port = includeListener ? incoming.Port : current.Port,
                StartOnLaunch = includeListener ? incoming.StartOnLaunch : current.StartOnLaunch,
                DefaultOutputId = incoming.DefaultOutputId,
                Outputs = outputs,
                Inputs = [.. incoming.Inputs],
            };
        });
    }

    /// <summary>
    /// Every output's upstream key in plaintext, by output id, for writing into an encrypted archive. Keys
    /// that cannot be unwrapped (saved by another user or on another OS) are left out rather than failing.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExportApiKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var output in Snapshot.Outputs)
        {
            try
            {
                if (ReadApiKey(output) is { Length: > 0 } key)
                    keys[output.Id] = key;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Unreadable here means unusable here; the destination machine cannot do better with it.
            }
        }
        return keys;
    }

    /// <summary>
    /// The route for a presented key, or null.
    ///
    /// <para>Two questions, asked in order and on every call: the directory says who holds the key and
    /// whether it is still allowed in, and this document says whether that user may use the Router. A
    /// user with no input is refused rather than falling back to the default output — reaching a tool
    /// and reaching a model are separate grants.</para>
    /// </summary>
    public RouterRoute? Resolve(string key)
    {
        if (LoadError is not null) return null;
        if (_users.Resolve(key) is not { } user) return null;
        lock (_gate)
        {
            var input = _current.Inputs.FirstOrDefault(i => string.Equals(i.UserId, user.Id, StringComparison.Ordinal));
            if (input is null) return null;
            var outputId = input.OutputId ?? _current.DefaultOutputId;
            var output = _current.Outputs.FirstOrDefault(o => o.Id == outputId);
            return new(user.Id, output) { ReadApiKey = () => output is null ? null : ReadApiKey(output) };
        }
    }

    /// <summary>
    /// Adopts the legacy inputs into the user directory and returns the routes that replace them. Called
    /// at load, before validation, because a document carrying identity is no longer valid.
    ///
    /// <para>Written back on the next save rather than immediately: a read that rewrites the file is a
    /// surprise, and the in-memory routes are already right. Needing a writable directory is why a
    /// read-only one reports instead — migrating is a change, and a mounted document cannot take one.</para>
    /// </summary>
    private RouterInput[] Migrate()
    {
        if (_users is not IWritableUsers writable)
            throw new ArgumentException("Router configuration still carries its own agent keys and the user directory is read-only.");
        try
        {
            return RouterUserMigration.Migrate(_path, writable);
        }
        catch (InvalidOperationException ex)
        {
            // The directory refusing to be written — users.json unreadable, say. Turned into the kind of
            // failure this constructor reports, so the hub comes up routing nobody and saying why rather
            // than throwing out of a store somebody is only reading.
            throw new ArgumentException(ex.Message, ex);
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
