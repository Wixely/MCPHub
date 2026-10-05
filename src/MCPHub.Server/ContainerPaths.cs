using MCPHub.Core.Infrastructure;

namespace MCPHub.Server;

/// <summary>
/// Where a headless hub keeps its files: told, not guessed.
///
/// <para>The desktop derives its paths from a user profile, which a container has no useful version
/// of — <c>/root/.config</c> in an image that runs as nobody, or a layer that disappears on the next
/// <c>docker run</c>. So these come from the environment and default to <c>/config</c> and
/// <c>/data</c>, which is what the Dockerfile declares and what an operator mounts.</para>
///
/// <para>Both are written to. Identity, permissions and routes are edited through the management
/// tools — that is the point of running this headless — so a read-only mount would be a hub that
/// reports every change as a failure. A deployment that wants policy fixed should mount the
/// read-only documents the deployment sources read instead.</para>
/// </summary>
public sealed class ContainerPaths : IAppPaths
{
    /// <summary>Where settings, users, permissions and routes live.</summary>
    public const string ConfigVariable = "MCPHUB_CONFIG_DIR";

    /// <summary>Where downloaded services and their working files live.</summary>
    public const string DataVariable = "MCPHUB_DATA_DIR";

    public ContainerPaths(Func<string, string?>? environment = null)
    {
        var read = environment ?? Environment.GetEnvironmentVariable;
        SettingsDirectory = Path.GetFullPath(Value(read, ConfigVariable) ?? "/config");
        DataDirectory = Path.GetFullPath(Value(read, DataVariable) ?? "/data");
    }

    public string SettingsDirectory { get; }

    public string DataDirectory { get; }

    public string DownloadsDirectory => Path.Combine(DataDirectory, "downloads");

    public string DefaultServersDirectory => Path.Combine(DataDirectory, "servers");

    public string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? Value(Func<string, string?> read, string name) =>
        read(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}
