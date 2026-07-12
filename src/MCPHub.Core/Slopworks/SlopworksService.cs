using MCPHub.Core.Models;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Slopworks;

/// <summary>
/// Install/update + release-check orchestration for Slopworks. Reuses the same GitHub release lookup,
/// asset selection, and download/extract pipeline as the MCP services and DaggerAgent. Unlike
/// <see cref="Agent.IAgentService"/>, we don't emit any config file on install — Slopworks manages
/// its own config under <c>%AppData%</c>.
/// </summary>
public interface ISlopworksService
{
    ManagedService Slopworks { get; }

    Task RefreshInstalledAsync(CancellationToken cancellationToken = default);

    Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task InstallOrUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SlopworksService : ISlopworksService
{
    private readonly SlopworksContext _context;
    private readonly IReleaseService _releaseService;
    private readonly IDownloadService _downloadService;
    private readonly IInstalledManifestStore _manifestStore;
    private readonly ISettingsStore _settings;
    private readonly ILogger<SlopworksService> _logger;

    public SlopworksService(
        SlopworksContext context,
        IReleaseService releaseService,
        IDownloadService downloadService,
        IInstalledManifestStore manifestStore,
        ISettingsStore settings,
        ILogger<SlopworksService> logger)
    {
        _context = context;
        _releaseService = releaseService;
        _downloadService = downloadService;
        _manifestStore = manifestStore;
        _settings = settings;
        _logger = logger;
    }

    public ManagedService Slopworks => _context.Slopworks;

    public async Task RefreshInstalledAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _manifestStore.ReadAsync(Slopworks.InstallFolder, cancellationToken);
        Slopworks.InstalledVersion = installed.TryGetValue(Slopworks.Catalog.Name, out var version) ? version
            : Slopworks.IsInstalled ? "unknown"
            : null;
        Slopworks.UpdateStatus = UpdateStatusCalculator.Compute(Slopworks.InstalledVersion, Slopworks.LatestVersion);
    }

    public async Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var release = await _releaseService.GetLatestReleaseAsync(Slopworks.Catalog, cancellationToken);
        if (release is not null)
            Slopworks.LatestVersion = release.Version;
        Slopworks.UpdateStatus = UpdateStatusCalculator.Compute(Slopworks.InstalledVersion, Slopworks.LatestVersion);
        return release;
    }

    public async Task InstallOrUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var release = await _releaseService.GetLatestReleaseAsync(Slopworks.Catalog, cancellationToken)
            ?? throw new InvalidOperationException("No Slopworks release found on GitHub.");
        Slopworks.LatestVersion = release.Version;

        // Slopworks only ships one asset per OS (no self-contained / framework-dependent split), but
        // ReleaseAssetSelector still needs a PublishFlavor argument — SelfContained is closer to
        // reality (the shipped exe is a single-file self-extract) and the AssetFileNameOverride on
        // the catalog entry ignores the flavour token anyway.
        var flavor = PublishFlavor.SelfContained;
        var asset = ReleaseAssetSelector.Select(Slopworks.Catalog, release, flavor)
            ?? throw new InvalidOperationException($"No Slopworks asset for this OS in {release.Version}.");

        _logger.LogInformation("Installing Slopworks {Version} from {Asset}", release.Version, asset.Name);
        await _downloadService.InstallAsync(Slopworks, asset, release.Version, progress, cancellationToken);
    }
}
