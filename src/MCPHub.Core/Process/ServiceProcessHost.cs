using System.Collections.Concurrent;
using System.Net.Http;
using MCPHub.Core.Logging;
using MCPHub.Core.Models;
using MCPHub.Processes;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Process;

/// <summary>
/// Starts/stops managed MCP sub-servers as hidden child processes, pipes their stdout/stderr into the
/// <see cref="ILogStore"/>, and drives <see cref="ServiceRunState"/> via a periodic <c>/healthz</c> probe.
/// </summary>
public interface IServiceProcessHost : IAsyncDisposable
{
    /// <summary>Starts the service hidden (no-op if already running). Transitions to Starting → Running.</summary>
    Task StartAsync(ManagedService service, CancellationToken cancellationToken = default);

    /// <summary>Requests a stop and kills the process tree. Transitions to Stopping → Stopped.</summary>
    Task StopAsync(ManagedService service, CancellationToken cancellationToken = default);

    /// <summary>Stops every running service (used on app shutdown).</summary>
    Task StopAllAsync();

    bool IsRunning(string serviceName);

    /// <summary>Raised whenever a service's run-state changes (on a background thread).</summary>
    event Action<ManagedService>? StateChanged;
}

/// <summary>
/// Adapter between the app's <see cref="ManagedService"/> model and the generic
/// <see cref="ProcessHost"/> from MCPHub.Processes: maps catalog/install-folder settings onto a
/// <see cref="ProcessSpec"/> at start, and process events back onto the service's mutable state.
/// </summary>
public sealed class ServiceProcessHost : IServiceProcessHost
{
    /// <summary>Name of the configured short-timeout <see cref="HttpClient"/> used for health probes.</summary>
    public const string HealthClientName = "health";

    private readonly ProcessHost _host;
    private readonly ILogStore _logStore;
    private readonly ConcurrentDictionary<string, ManagedService> _services = new(StringComparer.OrdinalIgnoreCase);

    public ServiceProcessHost(ILogStore logStore, IHttpClientFactory httpClientFactory, ILogger<ServiceProcessHost> logger)
    {
        _logStore = logStore;
        _host = new ProcessHost(new ProcessHostOptions
        {
            HealthClientFactory = () => httpClientFactory.CreateClient(HealthClientName),
        }, logger);

        _host.OutputReceived += line =>
            _logStore.Append(line.Name, new LogLine(line.Timestamp, MapStream(line.Stream), line.Text));

        _host.StateChanged += change =>
        {
            if (!_services.TryGetValue(change.Name, out var service))
                return;

            if (change.State == ProcessRunState.Starting)
                service.StartedAt = DateTimeOffset.Now;
            service.ProcessId = change.ProcessId;
            service.RunState = MapState(change.State);
            StateChanged?.Invoke(service);
        };
    }

    /// <inheritdoc />
    public event Action<ManagedService>? StateChanged;

    /// <inheritdoc />
    public bool IsRunning(string serviceName) => _host.IsRunning(serviceName);

    /// <inheritdoc />
    public Task StartAsync(ManagedService service, CancellationToken cancellationToken = default)
    {
        _services[service.Catalog.Name] = service;

        // Follow the server's own config for the effective port; fall back to the catalog default.
        service.Port = ServerConfigReader.ReadPort(service.ConfigPath) ?? service.Catalog.DefaultPort;

        return _host.StartAsync(new ProcessSpec
        {
            Name = service.Catalog.Name,
            ExecutablePath = service.ExecutablePath,
            WorkingDirectory = service.InstallFolder,
            HealthUrl = service.HealthUrl is { } url ? new Uri(url) : null,
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(ManagedService service, CancellationToken cancellationToken = default)
    {
        _services[service.Catalog.Name] = service;
        return _host.StopAsync(service.Catalog.Name, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAllAsync() => _host.StopAllAsync();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static LogStream MapStream(ProcessOutputStream stream) => stream switch
    {
        ProcessOutputStream.Stdout => LogStream.Stdout,
        ProcessOutputStream.Stderr => LogStream.Stderr,
        _ => LogStream.Info,
    };

    private static ServiceRunState MapState(ProcessRunState state) => state switch
    {
        ProcessRunState.Stopped => ServiceRunState.Stopped,
        ProcessRunState.Starting => ServiceRunState.Starting,
        ProcessRunState.Running => ServiceRunState.Running,
        ProcessRunState.Unhealthy => ServiceRunState.Unhealthy,
        ProcessRunState.Stopping => ServiceRunState.Stopping,
        _ => ServiceRunState.Faulted,
    };
}
