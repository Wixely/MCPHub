using MCPHub.Core.Logging;
using MCPHub.Core.Models;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Agent;

/// <summary>
/// Install/update + release-check orchestration for DaggerAgent. Reuses the same GitHub release lookup,
/// asset selection, download/extract and config-merge pipeline as the MCP services, then points the
/// freshly-installed <c>appsettings.json</c> at the MCPHub proxy (replacing the shipped example on a
/// first install; adding/refreshing the entry on later installs while keeping the user's config).
/// </summary>
public interface IAgentService
{
    ManagedService Agent { get; }

    Task RefreshInstalledAsync(CancellationToken cancellationToken = default);

    Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task InstallOrUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentService : IAgentService
{
    private readonly AgentContext _context;
    private readonly IReleaseService _releaseService;
    private readonly IDownloadService _downloadService;
    private readonly IInstalledManifestStore _manifestStore;
    private readonly IAgentProcessHost _processHost;
    private readonly ISettingsStore _settings;
    private readonly ILogStore _logStore;
    private readonly ILogger<AgentService> _logger;

    public AgentService(
        AgentContext context,
        IReleaseService releaseService,
        IDownloadService downloadService,
        IInstalledManifestStore manifestStore,
        IAgentProcessHost processHost,
        ISettingsStore settings,
        ILogStore logStore,
        ILogger<AgentService> logger)
    {
        _context = context;
        _releaseService = releaseService;
        _downloadService = downloadService;
        _manifestStore = manifestStore;
        _processHost = processHost;
        _settings = settings;
        _logStore = logStore;
        _logger = logger;
    }

    public ManagedService Agent => _context.Agent;

    public async Task RefreshInstalledAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _manifestStore.ReadAsync(Agent.InstallFolder, cancellationToken);
        Agent.InstalledVersion = installed.TryGetValue(Agent.Catalog.Name, out var version) ? version
            : Agent.IsInstalled ? "unknown"
            : null;
        Agent.UpdateStatus = UpdateStatusCalculator.Compute(Agent.InstalledVersion, Agent.LatestVersion);
    }

    public async Task<ReleaseInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var release = await _releaseService.GetLatestReleaseAsync(Agent.Catalog, cancellationToken);
        if (release is not null)
            Agent.LatestVersion = release.Version;
        Agent.UpdateStatus = UpdateStatusCalculator.Compute(Agent.InstalledVersion, Agent.LatestVersion);
        return release;
    }

    public async Task InstallOrUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var wasInstalled = Agent.IsInstalled;

        var release = await _releaseService.GetLatestReleaseAsync(Agent.Catalog, cancellationToken)
            ?? throw new InvalidOperationException("No DaggerAgent release found on GitHub.");
        Agent.LatestVersion = release.Version;

        var flavor = _settings.Current.Flavor;
        var asset = ReleaseAssetSelector.Select(Agent.Catalog, release, flavor)
            ?? throw new InvalidOperationException($"No {flavor} DaggerAgent asset for this OS in {release.Version}.");

        // Stop a running serve first so its executable isn't locked during the copy.
        if (_processHost.IsServing)
            await _processHost.StopAsync(cancellationToken);

        await _downloadService.InstallAsync(Agent, asset, release.Version, progress, cancellationToken);

        WireProxyIntoConfig(replaceExisting: !wasInstalled);
    }

    /// <summary>Points the installed <c>appsettings.json</c> at the MCPHub proxy's MCP endpoint.</summary>
    private void WireProxyIntoConfig(bool replaceExisting)
    {
        var configPath = Agent.ConfigPath;
        if (!File.Exists(configPath))
            return;

        try
        {
            var s = _settings.Current;
            var url = AgentProxyConfigurator.ProxyUrl(s.ProxyBindAddress, s.ProxyPort);
            var wired = AgentProxyConfigurator.WireProxy(File.ReadAllText(configPath), url, replaceExisting);

            var tmp = configPath + ".tmp";
            File.WriteAllText(tmp, wired);
            File.Move(tmp, configPath, overwrite: true);

            _logStore.Append(DaggerAgent.LogKey, new LogLine(DateTimeOffset.Now, LogStream.Info,
                $"Wired MCP config to the MCPHub proxy at {url}."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to wire the MCPHub proxy into DaggerAgent's config.");
            _logStore.Append(DaggerAgent.LogKey, new LogLine(DateTimeOffset.Now, LogStream.Info,
                "Could not auto-wire the MCPHub proxy into appsettings.json — set Mcp.Servers manually via Config."));
        }
    }
}
