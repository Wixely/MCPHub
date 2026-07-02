using MCPHub.Core.Catalog;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Logging;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;
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

    /// <summary>
    /// Applies the last-known latest version from the persisted release cache (no network request),
    /// updating the service's status. Returns <see langword="true"/> if a cached version was found.
    /// </summary>
    bool ApplyCachedLatest(ManagedService service);

    /// <summary>
    /// Downloads the latest release (in the configured <see cref="Flavor"/>) and installs it into the
    /// shared folder, preserving the user's config. Reports a 0..1 download fraction via
    /// <paramref name="progress"/>. Throws if no release/asset is available.
    /// </summary>
    Task InstallOrUpdateAsync(ManagedService service, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ServiceManager : IServiceManager
{
    private readonly IReleaseService _releaseService;
    private readonly IInstalledManifestStore _manifestStore;
    private readonly IDownloadService _downloadService;
    private readonly ILogStore _logStore;
    private readonly ILogger<ServiceManager> _logger;
    private readonly List<ManagedService> _services;

    public ServiceManager(
        IReleaseService releaseService,
        IInstalledManifestStore manifestStore,
        IDownloadService downloadService,
        ILogStore logStore,
        IAppPaths appPaths,
        ISettingsStore settings,
        ILogger<ServiceManager> logger)
    {
        _releaseService = releaseService;
        _manifestStore = manifestStore;
        _downloadService = downloadService;
        _logStore = logStore;
        _logger = logger;

        var current = settings.Current;
        ServersFolder = string.IsNullOrWhiteSpace(current.SharedServersFolder)
            ? appPaths.DefaultServersDirectory
            : current.SharedServersFolder;
        Flavor = current.Flavor;
        _services = ServiceCatalog.All.Select(entry => new ManagedService(entry, ServersFolder)).ToList();
    }

    public IReadOnlyList<ManagedService> Services => _services;

    public string ServersFolder { get; }

    public PublishFlavor Flavor { get; set; }

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

            // Follow the installed server's own config for the effective port; fall back to the default.
            service.Port = ServerConfigReader.ReadPort(service.ConfigPath) ?? service.Catalog.DefaultPort;
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

    public bool ApplyCachedLatest(ManagedService service)
    {
        var cached = _releaseService.GetCachedRelease(service.Catalog);
        if (cached is null)
            return false;

        service.LatestVersion = cached.Version;
        service.UpdateStatus = UpdateStatusCalculator.Compute(service.InstalledVersion, service.LatestVersion);
        return true;
    }

    public async Task InstallOrUpdateAsync(ManagedService service, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var catalog = service.Catalog;

        ReleaseInfo? release;
        try
        {
            release = await _releaseService.GetLatestReleaseAsync(catalog, cancellationToken);
        }
        catch (GithubAuthException)
        {
            _logStore.Append(catalog.Name, new LogLine(DateTimeOffset.Now, LogStream.Info,
                "GitHub rejected your token (401). Clear or replace it in Settings → GitHub token."));
            throw;
        }

        if (release is null)
        {
            _logStore.Append(catalog.Name, new LogLine(DateTimeOffset.Now, LogStream.Info,
                "No GitHub release available to install."));
            throw new InvalidOperationException($"No release found for {catalog.Name}.");
        }

        service.LatestVersion = release.Version;

        var asset = ReleaseAssetSelector.Select(catalog, release, Flavor);
        if (asset is null)
        {
            _logStore.Append(catalog.Name, new LogLine(DateTimeOffset.Now, LogStream.Info,
                $"No {Flavor} asset for this OS in {catalog.Name} {release.Version}."));
            throw new InvalidOperationException($"No matching {Flavor} asset for {catalog.Name} {release.Version}.");
        }

        await _downloadService.InstallAsync(service, asset, release.Version, progress, cancellationToken);
    }
}
