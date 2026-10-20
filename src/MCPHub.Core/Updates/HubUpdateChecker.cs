using MCPHub.Core.Models;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Updates;

/// <summary>
/// Looks for a newer MCPHub on its own, on the schedule <see cref="HubUpdatePolicy"/> sets: once at startup
/// if the last check is old enough, then once an hour re-asking the same question, because a hub that sits
/// in the tray for weeks never restarts. A check that finds a newer release raises <see cref="UpdateAvailable"/>;
/// one that fails is logged and tried again at the next tick, since the cache records only checks that
/// got an answer.
/// </summary>
public sealed class HubUpdateChecker : IDisposable
{
    /// <summary>How long after startup the first check waits, so it does not compete with the servers starting.</summary>
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(20);

    /// <summary>How often a running hub re-evaluates whether a check is due.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromHours(1);

    private readonly IReleaseService _releases;
    private readonly ISettingsStore _settings;
    private readonly ILogger<HubUpdateChecker> _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HubUpdateChecker(IReleaseService releases, ISettingsStore settings, ILogger<HubUpdateChecker> logger, TimeProvider? time = null)
    {
        _releases = releases;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised, on a background thread, when a check finds a release newer than the running build.</summary>
    public event Action<ReleaseInfo>? UpdateAvailable;

    /// <summary>When MCPHub last got an answer from GitHub about its own releases, by any check.</summary>
    public DateTimeOffset? LastCheckedAt => _releases.GetLastCheckedAt(HubRelease.Catalog);

    /// <summary>Whether automatic checks are switched on in settings.</summary>
    public bool IsEnabled => _settings.Current.AutoCheckForHubUpdates;

    /// <summary>Whether a check would run now.</summary>
    public bool IsDue() => IsEnabled && HubUpdatePolicy.IsCheckDue(LastCheckedAt, _time.GetUtcNow());

    /// <summary>When the next automatic check falls; <see langword="null"/> when disabled or due now.</summary>
    public DateTimeOffset? NextCheckAt => IsEnabled ? HubUpdatePolicy.NextCheckAt(LastCheckedAt, _time.GetUtcNow()) : null;

    /// <summary>Begins the background schedule. Safe to call once; later calls are ignored.</summary>
    public void Start(TimeSpan? initialDelay = null, TimeSpan? period = null)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;
        _ = LoopAsync(initialDelay ?? DefaultInitialDelay, period ?? DefaultPeriod);
    }

    private int _started;

    /// <summary>
    /// Runs a check if one is due and returns what GitHub said, or <see langword="null"/> when nothing ran or
    /// nothing came back. Concurrent callers wait their turn rather than double-spending the rate limit.
    /// </summary>
    public async Task<ReleaseInfo?> RunIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDue())
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsDue())
                return null;

            _logger.LogInformation("Checking for a newer MCPHub (last check: {Last}).", LastCheckedAt?.ToString("u") ?? "never");
            var release = await _releases.GetLatestReleaseAsync(HubRelease.Catalog, cancellationToken);
            if (release is null)
                return null;

            if (UpdateStatusCalculator.Compute(HubRelease.CurrentVersion, release.Version) == UpdateStatus.UpdateAvailable)
            {
                _logger.LogInformation("MCPHub {Version} is available (running {Current}).", release.Version, HubRelease.CurrentVersion);
                UpdateAvailable?.Invoke(release);
            }

            return release;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The automatic MCPHub update check failed; it will be tried again later.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoopAsync(TimeSpan initialDelay, TimeSpan period)
    {
        var token = _stopping.Token;
        try
        {
            await Task.Delay(initialDelay, token);
            await RunIfDueAsync(token);

            using var timer = new PeriodicTimer(period);
            while (await timer.WaitForNextTickAsync(token))
                await RunIfDueAsync(token);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
