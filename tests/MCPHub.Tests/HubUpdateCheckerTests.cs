using MCPHub.Core.Services.Github;
using MCPHub.Core.Settings;
using MCPHub.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static MCPHub.Tests.ManagementTestKit;

namespace MCPHub.Tests;

/// <summary>The automatic check: it runs only when due, says when a newer build exists, and never throws.</summary>
public class HubUpdateCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static (HubUpdateChecker Checker, FakeReleaseService Releases, FakeSettings Settings) Make(DateTimeOffset? lastChecked, string? latest)
    {
        var releases = new FakeReleaseService();
        releases.LastChecked["MCPHub"] = lastChecked;
        if (latest is not null)
            releases.Releases["MCPHub"] = Release(latest, "MCPHub-win-x64-v" + latest + ".zip");
        var settings = new FakeSettings();
        var checker = new HubUpdateChecker(releases, settings, NullLogger<HubUpdateChecker>.Instance, new FixedTime(Now));
        return (checker, releases, settings);
    }

    [Fact]
    public async Task Never_checked_runs_and_announces_a_newer_release()
    {
        var (checker, releases, _) = Make(lastChecked: null, latest: "99.0.0");
        ReleaseInfo? announced = null;
        checker.UpdateAvailable += r => announced = r;

        Assert.True(checker.IsDue());
        var result = await checker.RunIfDueAsync();

        Assert.Equal("99.0.0", result?.Version);
        Assert.Equal("99.0.0", announced?.Version);
        Assert.Equal(1, releases.Calls);
    }

    [Fact]
    public async Task A_recent_check_is_not_repeated()
    {
        var (checker, releases, _) = Make(lastChecked: Now.AddHours(-5), latest: "99.0.0");

        Assert.False(checker.IsDue());
        Assert.Null(await checker.RunIfDueAsync());
        Assert.Equal(0, releases.Calls);
        Assert.Equal(Now.AddHours(43), checker.NextCheckAt);
    }

    [Fact]
    public async Task A_stale_check_is_repeated()
    {
        var (checker, releases, _) = Make(lastChecked: Now.AddDays(-3), latest: "99.0.0");

        Assert.NotNull(await checker.RunIfDueAsync());
        Assert.Equal(1, releases.Calls);
    }

    [Fact]
    public async Task Switched_off_in_settings_means_no_check_and_no_next_time()
    {
        var (checker, releases, settings) = Make(lastChecked: null, latest: "99.0.0");
        settings.Current.AutoCheckForHubUpdates = false;

        Assert.False(checker.IsDue());
        Assert.Null(checker.NextCheckAt);
        Assert.Null(await checker.RunIfDueAsync());
        Assert.Equal(0, releases.Calls);
    }

    [Fact]
    public async Task A_release_that_is_not_newer_is_not_announced()
    {
        var (checker, _, _) = Make(lastChecked: null, latest: "0.0.1");
        var announced = false;
        checker.UpdateAvailable += _ => announced = true;

        var result = await checker.RunIfDueAsync();

        Assert.Equal("0.0.1", result?.Version);
        Assert.False(announced);
    }

    [Fact]
    public async Task A_failed_check_is_swallowed_and_logged_not_thrown()
    {
        var (checker, releases, _) = Make(lastChecked: null, latest: null);
        releases.Throws = new InvalidOperationException("github is down");

        Assert.Null(await checker.RunIfDueAsync());
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public MCPHubSettings Current { get; } = new();
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
