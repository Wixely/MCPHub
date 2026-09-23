using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPHub.Core.Routing;

/// <summary>Read-only, portable configuration for headless hosts. Never reads or writes desktop settings.</summary>
public sealed class RouterDeploymentSource : IRouterConfigurationSource
{
    private readonly string _path;
    private readonly Func<string, string?> _environment;
    private DeploymentState _state;
    private string? _reloadError;
    private sealed record DeploymentState(RouterConfiguration Configuration, IReadOnlyDictionary<string, string?> Keys);

    public RouterDeploymentSource(string path, Func<string, string?>? environment = null)
    {
        _path = Path.GetFullPath(path);
        _environment = environment ?? Environment.GetEnvironmentVariable;
        try { _state = Read(); }
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

    public RouterRoute? Resolve(string key)
    {
        if (key.Length is < 16 or > 256) return null;
        var state = Volatile.Read(ref _state);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var input = state.Configuration.Inputs.FirstOrDefault(i => i.Enabled &&
            CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(i.KeyHash)));
        if (input is null) return null;
        var output = state.Configuration.Outputs.FirstOrDefault(o => o.Id == (input.OutputId ?? state.Configuration.DefaultOutputId));
        var apiKey = output is null ? null : state.Keys[output.Id];
        return new(input.Id, output) { ReadApiKey = () => apiKey };
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
        var bindAddress = _environment("MCPHUB_ROUTER_BIND") ?? config.BindAddress;
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
            var secret = ReadSecret(i.KeyFile, i.KeyEnvironmentVariable, required: i.KeyHash is null);
            if (secret is not null && i.KeyHash is not null) throw new ArgumentException("Choose one input credential source.");
            if (secret is not null && secret.Length is < 32 or > 256) throw new ArgumentException("Input keys must contain 32–256 characters.");
            return new RouterInput
            {
                Id = i.Id, Name = i.Name, Enabled = i.Enabled, OutputId = i.OutputId,
                KeyHash = i.KeyHash ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret!))),
            };
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
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string? OutputId { get; init; }
    public string? KeyHash { get; init; }
    public string? KeyFile { get; init; }
    public string? KeyEnvironmentVariable { get; init; }
}
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
