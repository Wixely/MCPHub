using System.Text.Json;

namespace MCPHub.Core.Services;

/// <summary>Reads/writes MCPHub's record of which product version is installed in the shared folder.</summary>
public interface IInstalledManifestStore
{
    /// <summary>Reads <c>{serversFolder}/.mcphub/installed.json</c>; returns empty if missing or unreadable.</summary>
    Task<IReadOnlyDictionary<string, string>> ReadAsync(string serversFolder, CancellationToken cancellationToken = default);

    /// <summary>Records the installed version of one product and persists the manifest atomically.</summary>
    Task SetVersionAsync(string serversFolder, string serviceName, string version, CancellationToken cancellationToken = default);

    /// <summary>Removes a product from the manifest (e.g. after uninstall).</summary>
    Task RemoveAsync(string serversFolder, string serviceName, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class InstalledManifestStore : IInstalledManifestStore
{
    private const string ManifestDirName = ".mcphub";
    private const string ManifestFileName = "installed.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(string serversFolder, CancellationToken cancellationToken = default)
    {
        var path = ManifestPath(serversFolder);
        if (!File.Exists(path))
            return Empty();

        try
        {
            await using var stream = File.OpenRead(path);
            var map = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, Options, cancellationToken);
            return map is null ? Empty() : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return Empty();
        }
    }

    public async Task SetVersionAsync(string serversFolder, string serviceName, string version, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, string>(await ReadAsync(serversFolder, cancellationToken), StringComparer.OrdinalIgnoreCase)
        {
            [serviceName] = version,
        };
        await WriteAsync(serversFolder, map, cancellationToken);
    }

    public async Task RemoveAsync(string serversFolder, string serviceName, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, string>(await ReadAsync(serversFolder, cancellationToken), StringComparer.OrdinalIgnoreCase);
        if (map.Remove(serviceName))
            await WriteAsync(serversFolder, map, cancellationToken);
    }

    private static async Task WriteAsync(string serversFolder, Dictionary<string, string> map, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(serversFolder, ManifestDirName);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, ManifestFileName);
        var tmp = path + ".tmp";

        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, map, Options, cancellationToken);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private static string ManifestPath(string serversFolder) => Path.Combine(serversFolder, ManifestDirName, ManifestFileName);

    private static Dictionary<string, string> Empty() => new(StringComparer.OrdinalIgnoreCase);
}
