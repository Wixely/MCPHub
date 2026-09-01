using MCPHub.Core.Catalog;
using MCPHub.Core.Logging;
using MCPHub.Core.Management;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCPHub.Tests;

/// <summary>Test doubles for the agent-management tools: an in-memory service manager, process host and release service.</summary>
internal static class ManagementTestKit
{
    /// <summary>
    /// Process host whose start/stop land the service in a settable state at once (no real process). Records
    /// every call as <c>start:{Name}</c> / <c>stop:{Name}</c>; <see cref="BlockStart"/> holds a start open so
    /// a test can overlap two operations.
    /// </summary>
    public sealed class FakeProcessHost : IServiceProcessHost
    {
        private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = [];

        /// <summary>State a started service lands in (default Running).</summary>
        public ServiceRunState StartResult { get; set; } = ServiceRunState.Running;

        /// <summary>When set, <see cref="StartAsync"/> awaits this before doing anything.</summary>
        public TaskCompletionSource? BlockStart { get; set; }

        public event Action<ManagedService>? StateChanged;

        public bool IsRunning(string serviceName) => _running.Contains(serviceName);

        public async Task StartAsync(ManagedService service, CancellationToken cancellationToken = default)
        {
            if (BlockStart is { } gate)
                await gate.Task.WaitAsync(cancellationToken);

            var name = service.Catalog.Name;
            Calls.Add("start:" + name);
            service.RunState = StartResult;
            if (StartResult is ServiceRunState.Starting or ServiceRunState.Running or ServiceRunState.Unhealthy)
                _running.Add(name);
            else
                _running.Remove(name);
            service.Port ??= 5710;
            service.ProcessId = 4242;
            service.StartedAt = DateTimeOffset.Now;
            StateChanged?.Invoke(service);
        }

        public Task StopAsync(ManagedService service, CancellationToken cancellationToken = default)
        {
            var name = service.Catalog.Name;
            Calls.Add("stop:" + name);
            _running.Remove(name);
            service.RunState = ServiceRunState.Stopped;
            service.ProcessId = null;
            StateChanged?.Invoke(service);
            return Task.CompletedTask;
        }

        public Task StopAllAsync()
        {
            _running.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>
    /// Service manager over a handful of catalog products in a temp folder. "Installed" means the executable
    /// file exists there (as <see cref="ManagedService.IsInstalled"/> checks); releases come from <see cref="Latest"/>.
    /// </summary>
    public sealed class FakeServiceManager : IServiceManager
    {
        private readonly FakeProcessHost _host;

        public FakeServiceManager(string folder, FakeProcessHost host, params string[] catalogNames)
        {
            _host = host;
            ServersFolder = folder;
            Services = catalogNames
                .Select(n => new ManagedService(ServiceCatalog.FindByName(n) ?? throw new ArgumentException($"Unknown catalog entry {n}"), folder))
                .ToList();
        }

        public List<ManagedService> Services { get; }

        IReadOnlyList<ManagedService> IServiceManager.Services => Services;

        public string ServersFolder { get; }

        public PublishFlavor Flavor { get; set; }

        /// <summary>Latest release per catalog name; absent or null = GitHub unreachable / no release.</summary>
        public Dictionary<string, ReleaseInfo?> Latest { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every install performed, as <c>{Name}:{version}</c>.</summary>
        public List<string> Installs { get; } = [];

        public int RefreshCount { get; private set; }

        public event Action<ManagedService>? ServiceChanged;

        public ManagedService this[string name] => Services.Single(s => string.Equals(s.Catalog.Name, name, StringComparison.OrdinalIgnoreCase));

        public Task RefreshInstalledAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            foreach (var service in Services)
            {
                if (!service.IsInstalled)
                    service.InstalledVersion = null;
                service.UpdateStatus = UpdateStatusCalculator.Compute(service.InstalledVersion, service.LatestVersion);
            }
            return Task.CompletedTask;
        }

        public Task<ReleaseInfo?> CheckForUpdatesAsync(ManagedService service, CancellationToken cancellationToken = default)
        {
            var release = Latest.GetValueOrDefault(service.Catalog.Name);
            if (release is not null)
                service.LatestVersion = release.Version;
            service.UpdateStatus = UpdateStatusCalculator.Compute(service.InstalledVersion, service.LatestVersion);
            ServiceChanged?.Invoke(service);
            return Task.FromResult(release);
        }

        public bool ApplyCachedLatest(ManagedService service) => false;

        public async Task InstallOrUpdateAsync(ManagedService service, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            var name = service.Catalog.Name;
            var release = Latest.GetValueOrDefault(name) ?? throw new InvalidOperationException($"No release found for {name}.");

            // The real pipeline stops a running instance to unlock its executable.
            if (_host.IsRunning(name))
                await _host.StopAsync(service, cancellationToken);

            MarkInstalled(service, release.Version);
            Installs.Add($"{name}:{release.Version}");
            progress?.Report(1.0);
            ServiceChanged?.Invoke(service);
        }

        /// <summary>Puts the executable on disk and records the version, as a finished install would.</summary>
        public static void MarkInstalled(ManagedService service, string version)
        {
            Directory.CreateDirectory(service.InstallFolder);
            File.WriteAllText(service.ExecutablePath, string.Empty);
            service.InstalledVersion = version;
            service.LatestVersion ??= version;
            service.UpdateStatus = UpdateStatus.UpToDate;
        }
    }

    /// <summary>Release service answering from a dictionary keyed by catalog <see cref="ServiceCatalogEntry.Name"/>.</summary>
    public sealed class FakeReleaseService : IReleaseService
    {
        public Dictionary<string, ReleaseInfo?> Releases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Exception? Throws { get; set; }

        public Task<ReleaseInfo?> GetLatestReleaseAsync(ServiceCatalogEntry entry, CancellationToken cancellationToken = default)
            => Throws is { } ex ? Task.FromException<ReleaseInfo?>(ex) : Task.FromResult(Releases.GetValueOrDefault(entry.Name));

        public ReleaseInfo? GetCachedRelease(ServiceCatalogEntry entry) => null;
    }

    public static ReleaseInfo Release(string version, params string[] assetNames) => new(
        version, "v" + version, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), IsPrerelease: false,
        assetNames.Select(n => new ReleaseAsset(n, "https://example.invalid/" + n, 1234)).ToList());

    public static AgentManagementToolProvider Provider(FakeServiceManager manager, FakeProcessHost host, FakeReleaseService? releases = null, LogStore? logs = null)
        => new(manager, host, releases ?? new FakeReleaseService(), logs ?? new LogStore(100), NullLogger<AgentManagementToolProvider>.Instance);
}
