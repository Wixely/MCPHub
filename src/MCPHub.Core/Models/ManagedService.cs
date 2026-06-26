using MCPHub.Core.Catalog;

namespace MCPHub.Core.Models;

/// <summary>
/// Runtime state of one catalog product as installed/managed on this machine. Pairs the immutable
/// <see cref="ServiceCatalogEntry"/> with mutable install/version/process state. The UI layer wraps
/// this in an observable view-model; this type stays UI-framework-agnostic.
/// </summary>
public sealed class ManagedService
{
    public ManagedService(ServiceCatalogEntry catalog, string installFolder)
    {
        Catalog = catalog;
        InstallFolder = installFolder;
    }

    /// <summary>Immutable product description.</summary>
    public ServiceCatalogEntry Catalog { get; }

    /// <summary>Shared folder that holds every product's exe + config.</summary>
    public string InstallFolder { get; }

    /// <summary>Full path to the executable in the shared folder.</summary>
    public string ExecutablePath => Path.Combine(InstallFolder, Catalog.ExecutableFileName());

    /// <summary>Full path to the <c>{Name}.json</c> config in the shared folder.</summary>
    public string ConfigPath => Path.Combine(InstallFolder, Catalog.ConfigFileName);

    /// <summary>Effective HTTP port (from installed config when known, else the catalog default).</summary>
    public int? Port { get; set; }

    /// <summary>MCP endpoint URL once a port is known, e.g. <c>http://localhost:5710/mcp</c>.</summary>
    public string? EndpointUrl => Port is { } p ? $"http://localhost:{p}/mcp" : null;

    /// <summary>Health endpoint URL once a port is known, e.g. <c>http://localhost:5710/healthz</c>.</summary>
    public string? HealthUrl => Port is { } p ? $"http://localhost:{p}/healthz" : null;

    /// <summary>Installed version (from the MCPHub-owned manifest), or <see langword="null"/> if not installed.</summary>
    public string? InstalledVersion { get; set; }

    /// <summary>Latest release version discovered from GitHub, or <see langword="null"/> if unchecked.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>Whether the executable exists in the shared folder.</summary>
    public bool IsInstalled => File.Exists(ExecutablePath);

    public UpdateStatus UpdateStatus { get; set; } = UpdateStatus.Unknown;

    public ServiceRunState RunState { get; set; } = ServiceRunState.Stopped;

    /// <summary>Whether MCPHub should auto-start and aggregate this service.</summary>
    public bool Enabled { get; set; }

    /// <summary>OS process id while running.</summary>
    public int? ProcessId { get; set; }

    /// <summary>When the current process was started.</summary>
    public DateTimeOffset? StartedAt { get; set; }
}
