using MCPHub.Core.Agent;
using MCPHub.Core.Logging;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Slopworks;

/// <summary>
/// Result of a wire attempt — surfaced to the UI so it can show a status message and grey out
/// buttons appropriately.
/// </summary>
public sealed record BridgeResult(bool Wired, string Message);

/// <summary>
/// Cross-app orchestrator: takes the current Slopworks state (via <see cref="ISlopworksCli"/>) and
/// writes the vLLM endpoint into DaggerAgent's <c>appsettings.json</c> without touching anything
/// else. Both apps live behind their own <see cref="AgentContext"/> / <see cref="SlopworksContext"/>
/// so the bridge is the only place that knows about both.
/// </summary>
public interface ISlopworksDaggerBridge
{
    /// <summary>
    /// Snapshot of "is this actionable right now" — true iff DaggerAgent's appsettings.json
    /// exists (i.e. the agent is installed). Slopworks doesn't need to be installed here — the
    /// bridge can also be called just after Slopworks finishes installing, at which point its
    /// <c>status --json</c> is answerable.
    /// </summary>
    bool CanWire { get; }

    /// <summary>
    /// Adds (or refreshes) the current Slopworks-managed vLLM endpoint in DaggerAgent's
    /// <c>appsettings.json</c>. No-op if either app isn't installed. Idempotent: same Slopworks
    /// model results in the same endpoint <c>Id</c> and updates the existing entry in place.
    /// </summary>
    Task<BridgeResult> WireAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SlopworksDaggerBridge : ISlopworksDaggerBridge
{
    private readonly AgentContext _agentContext;
    private readonly SlopworksContext _slopworksContext;
    private readonly ISlopworksCli _cli;
    private readonly ILogStore _logStore;
    private readonly ILogger<SlopworksDaggerBridge> _logger;

    public SlopworksDaggerBridge(
        AgentContext agentContext,
        SlopworksContext slopworksContext,
        ISlopworksCli cli,
        ILogStore logStore,
        ILogger<SlopworksDaggerBridge> logger)
    {
        _agentContext = agentContext;
        _slopworksContext = slopworksContext;
        _cli = cli;
        _logStore = logStore;
        _logger = logger;
    }

    public bool CanWire => _agentContext.Agent.IsInstalled && _slopworksContext.Slopworks.IsInstalled;

    public async Task<BridgeResult> WireAsync(CancellationToken cancellationToken = default)
    {
        if (!_agentContext.Agent.IsInstalled)
            return new BridgeResult(false, "DaggerAgent isn't installed — skipping vLLM endpoint wiring.");
        if (!_slopworksContext.Slopworks.IsInstalled)
            return new BridgeResult(false, "Slopworks isn't installed — nothing to wire yet.");

        var configPath = _agentContext.Agent.ConfigPath;
        if (!File.Exists(configPath))
            return new BridgeResult(false, $"DaggerAgent config not found at {configPath}.");

        var status = await _cli.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status.Model) || status.Port <= 0 || string.IsNullOrWhiteSpace(status.Endpoint))
            return new BridgeResult(false, "Slopworks status is incomplete (no model / port / endpoint yet). Configure Slopworks first.");

        var descriptor = new SlopworksEndpointDescriptor(
            Id: SlopworksDaggerConfigurator.BuildId(status.Model),
            DisplayName: $"Slopworks vLLM ({status.Model})",
            BaseUrl: status.Endpoint,     // Slopworks emits the `/v1` suffix already.
            Model: status.Model);

        try
        {
            var original = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
            var wired = SlopworksDaggerConfigurator.WireEndpoint(original, descriptor);

            // Only rewrite when the effective JSON changed — a no-op wire (same model, same port)
            // shouldn't bump the file's mtime and shouldn't nudge DaggerAgent's config-file watcher.
            if (string.Equals(original, wired, StringComparison.Ordinal))
                return new BridgeResult(true, $"Slopworks endpoint '{descriptor.Id}' already up to date in DaggerAgent config.");

            var tmp = configPath + ".tmp";
            await File.WriteAllTextAsync(tmp, wired, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, configPath, overwrite: true);

            _logStore.Append(Slopworks.LogKey, new LogLine(DateTimeOffset.Now, LogStream.Info,
                $"Wired Slopworks vLLM endpoint '{descriptor.Id}' ({descriptor.BaseUrl}, model={descriptor.Model}) into {configPath}."));
            return new BridgeResult(true, $"Added / refreshed '{descriptor.Id}' in DaggerAgent's endpoints.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to wire Slopworks vLLM endpoint into DaggerAgent config at {Path}", configPath);
            return new BridgeResult(false, "Failed to update DaggerAgent config: " + ex.Message);
        }
    }
}
