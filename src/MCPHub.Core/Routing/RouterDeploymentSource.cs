using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MCPHub.Core.Users;

namespace MCPHub.Core.Routing;

/// <summary>Read-only, portable configuration for headless hosts. Never reads or writes desktop settings.</summary>
public sealed class RouterDeploymentSource : IRouterConfigurationSource
{
    private readonly string _path;
    private readonly IUserDirectory _users;
    private readonly Func<string, string?> _environment;
    private DeploymentState _state;
    private string? _reloadError;
    private sealed record DeploymentState(RouterConfiguration Configuration, IReadOnlyDictionary<string, string?> Keys);

    /// <param name="users">The mounted user directory. Identity used to be in this document, with each
    /// input naming a key file or variable of its own; it is a users document's business now, so one key
    /// serves the Router and the proxy and a suspension in that file stops both.</param>
    public RouterDeploymentSource(string path, IUserDirectory users, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(users);
        _users = users;
        _path = Path.GetFullPath(path);
        _environment = environment ?? Environment.GetEnvironmentVariable;
        try { _state = Read(); }
        catch (RouterMigrationRequiredException)
        {
            // Let this one past IsConfigurationError's net with its own message: it is the one startup
            // failure where the file is intact and the fix is in a different file.
            throw new InvalidOperationException(
                "Router deployment inputs carry their own agent keys, which moved to the users document. "
                + "Give each input a UserId and declare that user's key there.");
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            // No exception payload: JSON values, secret paths, or environment contents must not enter logs.
            throw new InvalidOperationException("Router deployment configuration is invalid or a referenced secret is unavailable.");
        }
    }

    public RouterConfiguration Snapshot
    {
        get
        {
            var current = Volatile.Read(ref _state).Configuration;
            return current with { Inputs = [.. current.Inputs], Outputs = [.. current.Outputs] };
        }
    }
    public string? LoadError => null; // Initial failure throws; reload failure retains the last valid snapshot.
    public string? ReloadError => Volatile.Read(ref _reloadError);

    /// <summary>Atomically refresh routes and secret files. A port change requires a host restart.</summary>
    public bool Reload()
    {
        try
        {
            var next = Read();
            if (next.Configuration.Port != Snapshot.Port || next.Configuration.BindAddress != Snapshot.BindAddress)
                throw new ArgumentException("Listener address and port changes require restart.");
            Volatile.Write(ref _state, next);
            Volatile.Write(ref _reloadError, null);
            return true;
        }
        catch (Exception ex) when (IsConfigurationError(ex))
        {
            Volatile.Write(ref _reloadError, "Router reload rejected. The last valid routes remain active; check configuration, secrets, and restart for listener changes.");
            return false;
        }
    }

    /// <inheritdoc cref="RouterStore.Resolve"/>
    public RouterRoute? Resolve(string key)
    {
        if (_users.Resolve(key) is not { } user) return null;
        var state = Volatile.Read(ref _state);
        var input = state.Configuration.Inputs.FirstOrDefault(i => string.Equals(i.UserId, user.Id, StringComparison.Ordinal));
        if (input is null) return null;
        var output = state.Configuration.Outputs.FirstOrDefault(o => o.Id == (input.OutputId ?? state.Configuration.DefaultOutputId));
        var apiKey = output is null ? null : state.Keys[output.Id];
        return new(user.Id, output) { ReadApiKey = () => apiKey };
    }

    private DeploymentState Read()
    {
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("Configuration is too large.");
        var config = JsonSerializer.Deserialize(File.ReadAllText(_path), RouterDeploymentJsonContext.Default.RouterDeploymentConfiguration)
            ?? throw new InvalidDataException("Empty configuration.");
        if (config.SchemaVersion != 1 || config.Inputs is null || config.Outputs is null)
            throw new ArgumentException("Invalid configuration.");
        var portText = _environment("MCPHUB_ROUTER_PORT");
        var port = portText is null ? config.Port : int.Parse(portText, NumberStyles.None, CultureInfo.InvariantCulture);
        // MCPHUB_ROUTER_BIND wins over the file, so one image can serve loopback or the container network.
        // Coerced, so a deployment file written before this setting existed still loads.
        var bindAddress = RouterConfigurationRules.CoerceBindAddress(_environment("MCPHUB_ROUTER_BIND") ?? config.BindAddress);
        var keys = new Dictionary<string, string?>(StringComparer.Ordinal);
        var outputs = config.Outputs.Select(o =>
        {
            if (o is null) throw new ArgumentException("Invalid output.");
            keys.Add(o.Id, ReadSecret(o.ApiKeyFile, o.ApiKeyEnvironmentVariable, required: false));
            return new RouterOutput { Id = o.Id, Name = o.Name, BaseUrl = RouterConfigurationRules.NormalizeBaseUrl(o.BaseUrl), Model = string.IsNullOrWhiteSpace(o.Model) ? null : o.Model.Trim() };
        }).ToArray();
        var inputs = config.Inputs.Select(i =>
        {
            if (i is null) throw new ArgumentException("Invalid input.");

            // Named by code rather than lumped in with "invalid configuration": a deployment file written
            // against the old shape is a migration, not a typo, and the operator needs to be sent to the
            // users document rather than left reading this one. No field value is quoted — those are keys.
            if (i.KeyHash is not null || i.KeyFile is not null || i.KeyEnvironmentVariable is not null || i.Name is not null)
                throw new RouterMigrationRequiredException();
            if (string.IsNullOrWhiteSpace(i.UserId)) throw new ArgumentException("Invalid input.");
            return new RouterInput { UserId = i.UserId, OutputId = i.OutputId };
        }).ToArray();
        var runtime = new RouterConfiguration { Port = port, BindAddress = bindAddress, Inputs = inputs, Outputs = outputs, DefaultOutputId = config.DefaultOutputId };
        RouterConfigurationRules.Validate(runtime);
        return new(runtime, keys);
    }

    private string? ReadSecret(string? file, string? environmentVariable, bool required)
    {
        if (file is not null && environmentVariable is not null) throw new ArgumentException("Choose one secret source.");
        string? value = null;
        if (file is not null)
        {
            var path = Path.GetFullPath(file, Path.GetDirectoryName(_path)!);
            if (new FileInfo(path).Length > 8192) throw new InvalidDataException("Secret is too large.");
            value = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        else if (environmentVariable is not null) value = _environment(environmentVariable);
        if (required || file is not null || environmentVariable is not null)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
                throw new ArgumentException("Missing or invalid secret.");
        }
        return value;
    }
    private static bool IsConfigurationError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException or OverflowException;
}

public sealed record RouterDeploymentConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public int Port { get; init; } = 5801;

    /// <summary>Listener address; overridden by <c>MCPHUB_ROUTER_BIND</c>. Containers usually want <c>0.0.0.0</c>.</summary>
    public string BindAddress { get; init; } = RouterConfigurationRules.Loopback;

    public string? DefaultOutputId { get; init; }
    public RouterDeploymentInput[] Inputs { get; init; } = [];
    public RouterDeploymentOutput[] Outputs { get; init; } = [];
}
public sealed record RouterDeploymentInput
{
    /// <summary>The user allowed through the Router — an id from the mounted users document.</summary>
    public string UserId { get; init; } = string.Empty;

    public string? OutputId { get; init; }

    // ---- refused, not ignored -------------------------------------------------------------------

    /// <summary>
    /// Identity as this document used to carry it. Still read, and only so that a file written against
    /// the old shape is refused by name: silently ignoring a key field would start a hub that let nobody
    /// in and said nothing about why.
    /// </summary>
    public string? Name { get; init; }

    /// <inheritdoc cref="Name"/>
    public string? KeyHash { get; init; }

    /// <inheritdoc cref="Name"/>
    public string? KeyFile { get; init; }

    /// <inheritdoc cref="Name"/>
    public string? KeyEnvironmentVariable { get; init; }
}

/// <summary>A deployment document written before identity moved to the users document.</summary>
internal sealed class RouterMigrationRequiredException() : Exception("Router inputs still carry keys.");
public sealed record RouterDeploymentOutput
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string? ApiKeyFile { get; init; }
    public string? ApiKeyEnvironmentVariable { get; init; }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RouterDeploymentConfiguration))]
internal sealed partial class RouterDeploymentJsonContext : JsonSerializerContext;
