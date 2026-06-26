using MCPHub.Core.Catalog;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Models;
using MCPHub.Core.Services.Github;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Services;

/// <summary>
/// Orchestrates the managed-service inventory: builds <see cref="ManagedService"/> objects from the
/// catalog, reconciles installed versions from the manifest, and checks GitHub for newer releases.
/// </summary>
public interface IServiceManager
{
    /// <summary>The managed services, one per catalog product.</summary>
    IReadOnlyList<ManagedService> Services { get; }

    /// <summary>The shared folder all products are installed into.</summary>
    string ServersFolder { get; }

    /// <summary>Release flavour to prefer when downloading (defaults to self-contained).</summary>
    PublishFlavor Flavor { get; set; }

    /// <summary>Reconciles installed versions (from the manifest) and recomputes update status.</summary>
    Task RefreshInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks GitHub for a service's latest release, updates its status, and returns the release.</summary>
    Task<ReleaseInfo?> CheckForUpdatesAsync(ManagedService service, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ServiceManager : IServiceManager
{
    private readonly IReleaseService _releaseService;
    private readonly IInstalledManifestStore _manifestStore;
    private readonly ILogger<ServiceManager> _logger;
    private readonly List<ManagedService> _services;

    public ServiceManager(
        IReleaseService releaseService,
        IInstalledManifestStore manifestStore,
        IAppPaths appPaths,
        ILogger<ServiceManager> logger)
    {
        _releaseService = releaseService;
        _manifestStore = manifestStore;
        _logger = logger;

        ServersFolder = appPaths.DefaultServersDirectory;
        _services = ServiceCatalog.All.Select(entry => new ManagedService(entry, ServersFolder)).ToList();
    }

    public IReadOnlyList<ManagedService> Services => _services;

    public string ServersFolder { get; }

    public PublishFlavor Flavor { get; set; } = PublishFlavor.SelfContained;

    public async Task RefreshInstalledAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _manifestStore.ReadAsync(ServersFolder, cancellationToken);

        foreach (var service in _services)
        {
            if (installed.TryGetValue(service.Catalog.Name, out var version))
                service.InstalledVersion = version;
            else if (service.IsInstalled)
                service.InstalledVersion = "unknown"; // present on disk but not recorded by MCPHub
            else
                service.InstalledVersion = null;

            service.Port ??= service.Catalog.DefaultPort;
            service.UpdateStatus = UpdateStatusCalculator.Compute(service.InstalledVersion, service.LatestVersion);
        }
    }

    public async Task<ReleaseInfo?> CheckForUpdatesAsync(ManagedService service, CancellationToken cancellationToken = default)
    {
        var release = await _releaseService.GetLatestReleaseAsync(service.Catalog, cancellationToken);
        if (release is not null)
        {
            service.LatestVersion = release.Version;
            _logger.LogInformation("{Service}: latest release is {Version}.", service.Catalog.Name, release.Version);
        }

        service.UpdateStatus = UpdateStatusCalculator.Compute(service.InstalledVersion, service.LatestVersion);
        return release;
    }
}
