using System.Text.Json;
using MCPHub.Core.Logging;
using MCPHub.Core.Management;
using MCPHub.Core.Models;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Updates;
using ModelContextProtocol.Protocol;
using Xunit;
using static MCPHub.Tests.ManagementTestKit;
using static MCPHub.Tests.ProxyTestKit;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

public class AgentManagementToolProviderTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new();

        public FakeProcessHost Host { get; } = new();
        public FakeServiceManager Manager { get; }
        public FakeReleaseService Releases { get; } = new();
        public LogStore Logs { get; } = new(100);
        public AgentManagementToolProvider Provider { get; }

        public ManagedService Kodi => Manager["KodiMCPSharp"];
        public ManagedService Redis => Manager["RedisMCPSharp"];

        public Fixture()
        {
            Manager = new FakeServiceManager(_dir.Path, Host, "KodiMCPSharp", "RedisMCPSharp");
            Provider = ManagementTestKit.Provider(Manager, Host, Releases, Logs);
        }

        public Task<CallToolResult> Call(string tool, string? json = null)
            => Provider.CallAsync(tool, json is null ? null : JsonArgs(json), CancellationToken.None).AsTask();

        public void Dispose() => _dir.Dispose();
    }

    private static Dictionary<string, JsonElement> JsonArgs(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static JsonElement Parse(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement;

    private static string Str(JsonElement e, string property) => e.GetProperty(property).GetString()!;

    [Fact]
    public void Advertises_the_eight_management_tools_with_object_schemas()
    {
        using var f = new Fixture();

        Assert.Equal("mcphub", f.Provider.Key);
        Assert.Equal(
            ["list_services", "start", "stop", "restart", "install", "update", "check_service_updates", "check_hub_update"],
            f.Provider.Tools.Select(t => t.Name));
        Assert.All(f.Provider.Tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.Equal("object", t.InputSchema.GetProperty("type").GetString());
        });

        // Every tool other than the inventory belongs to exactly one gated group.
        var grouped = AgentManagementToolProvider.ControlTools
            .Concat(AgentManagementToolProvider.InstallTools)
            .Concat(AgentManagementToolProvider.UpdateCheckTools)
            .ToList();
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.Equal(f.Provider.Tools.Select(t => t.Name).Where(n => n != AgentManagementToolProvider.ListTool).Order(), grouped.Order());
    }

    [Fact]
    public async Task List_services_reports_each_server_without_paths_or_config()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.2.0");
        f.Kodi.Port = 5730;

        var all = Parse(await f.Call("list_services"));

        Assert.Equal(2, all.GetProperty("count").GetInt32());
        Assert.Equal(1, f.Manager.RefreshCount);
        var kodi = all.GetProperty("services").EnumerateArray().Single(s => Str(s, "key") == "kodi");
        Assert.Equal("KodiMCPSharp", Str(kodi, "name"));
        Assert.True(kodi.GetProperty("installed").GetBoolean());
        Assert.Equal("1.2.0", Str(kodi, "installedVersion"));
        Assert.Equal("Stopped", Str(kodi, "runState"));
        Assert.Equal(5730, kodi.GetProperty("port").GetInt32());
        Assert.False(kodi.TryGetProperty("installFolder", out _));
        Assert.False(kodi.TryGetProperty("configPath", out _));
        Assert.DoesNotContain(f.Manager.ServersFolder, Text(await f.Call("list_services")));

        var redis = all.GetProperty("services").EnumerateArray().Single(s => Str(s, "key") == "redis");
        Assert.False(redis.GetProperty("installed").GetBoolean());
        Assert.Equal("NotInstalled", Str(redis, "updateStatus"));

        var filtered = Parse(await f.Call("list_services", """{ "query": "KODI" }"""));
        Assert.Equal(1, filtered.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Resolves_a_server_by_key_product_name_or_display_name_and_names_the_options_otherwise()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Redis, "1.0.0");

        foreach (var id in new[] { "redis", "RedisMCPSharp", "REDISMCPSHARP", "Redis" })
        {
            var result = await f.Call("start", $$"""{ "service": "{{id}}" }""");
            Assert.NotEqual(true, result.IsError);
            await f.Call("stop", $$"""{ "service": "{{id}}" }""");
        }

        var unknown = await f.Call("start", """{ "service": "nope" }""");
        Assert.True(unknown.IsError);
        Assert.Contains("kodi, redis", Text(unknown));

        var missing = await f.Call("start");
        Assert.True(missing.IsError);
        Assert.Equal("'service' is required.", Text(missing));
    }

    [Fact]
    public async Task Start_requires_an_install_then_brings_the_server_up_and_notes_it_in_the_log()
    {
        using var f = new Fixture();

        var notInstalled = await f.Call("start", """{ "service": "kodi" }""");
        Assert.True(notInstalled.IsError);
        Assert.Contains("mcphub__install", Text(notInstalled));
        Assert.Empty(f.Host.Calls);

        FakeServiceManager.MarkInstalled(f.Kodi, "1.2.0");
        var started = await f.Call("start", """{ "service": "kodi" }""");

        Assert.NotEqual(true, started.IsError);
        var json = Parse(started);
        Assert.Equal("start", Str(json, "action"));
        Assert.Contains("Running", Str(json, "message"));
        Assert.Equal("Running", Str(json.GetProperty("service"), "runState"));
        Assert.Equal(["start:KodiMCPSharp"], f.Host.Calls);
        Assert.Contains(f.Logs.Snapshot("KodiMCPSharp"), l => l.Text.Contains("mcphub__start"));

        var again = await f.Call("start", """{ "service": "kodi" }""");
        Assert.NotEqual(true, again.IsError);
        Assert.Contains("already Running", Text(again));
        Assert.Single(f.Host.Calls);
    }

    [Fact]
    public async Task A_start_that_faults_is_an_error_that_points_at_the_user()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.2.0");
        f.Host.StartResult = ServiceRunState.Faulted;

        var result = await f.Call("start", """{ "service": "kodi" }""");

        Assert.True(result.IsError);
        Assert.Contains("failed to start", Text(result));
        Assert.Contains("logs", Text(result));
    }

    [Fact]
    public async Task A_start_still_settling_after_the_wait_is_reported_not_failed()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.2.0");
        f.Host.StartResult = ServiceRunState.Starting;

        var result = await f.Call("start", """{ "service": "kodi", "wait_seconds": 0 }""");

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("still Starting", Text(result));
    }

    [Fact]
    public async Task Stop_and_restart_drive_the_process_host_in_order()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.2.0");

        var stopWhileStopped = await f.Call("stop", """{ "service": "kodi" }""");
        Assert.NotEqual(true, stopWhileStopped.IsError);
        Assert.Contains("already Stopped", Text(stopWhileStopped));
        Assert.Empty(f.Host.Calls);

        var restartFromStopped = Parse(await f.Call("restart", """{ "service": "kodi" }"""));
        Assert.StartsWith("Was not running", Str(restartFromStopped, "message"));
        Assert.False(restartFromStopped.GetProperty("wasRunning").GetBoolean());
        Assert.Equal(["start:KodiMCPSharp"], f.Host.Calls);

        f.Host.Calls.Clear();
        var restartFromRunning = Parse(await f.Call("restart", """{ "service": "kodi" }"""));
        Assert.StartsWith("Restarted", Str(restartFromRunning, "message"));
        Assert.True(restartFromRunning.GetProperty("wasRunning").GetBoolean());
        Assert.Equal(["stop:KodiMCPSharp", "start:KodiMCPSharp"], f.Host.Calls);

        f.Host.Calls.Clear();
        var stopped = Parse(await f.Call("stop", """{ "service": "kodi" }"""));
        Assert.Equal("Stopped", Str(stopped.GetProperty("service"), "runState"));
        Assert.Equal(["stop:KodiMCPSharp"], f.Host.Calls);
        Assert.Equal(ServiceRunState.Stopped, f.Kodi.RunState);
    }

    [Fact]
    public async Task Install_refuses_an_installed_server_and_installs_an_absent_one()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.2.0");

        var already = await f.Call("install", """{ "service": "kodi" }""");
        Assert.True(already.IsError);
        Assert.Contains("mcphub__update", Text(already));

        var noRelease = await f.Call("install", """{ "service": "redis" }""");
        Assert.True(noRelease.IsError);
        Assert.Contains("No release found", Text(noRelease));
        Assert.False(f.Redis.IsInstalled);

        f.Manager.Latest["RedisMCPSharp"] = Release("2.0.0");
        var installed = Parse(await f.Call("install", """{ "service": "redis" }"""));
        Assert.Equal(["RedisMCPSharp:2.0.0"], f.Manager.Installs);
        Assert.True(f.Redis.IsInstalled);
        Assert.Equal("2.0.0", Str(installed.GetProperty("service"), "installedVersion"));
        Assert.Equal("Stopped", Str(installed.GetProperty("service"), "runState"));
        Assert.Empty(f.Host.Calls);

        // And with start=true the freshly installed server is brought up too.
        File.Delete(f.Redis.ExecutablePath);
        f.Redis.InstalledVersion = null;
        var installedAndStarted = Parse(await f.Call("install", """{ "service": "redis", "start": true }"""));
        Assert.Equal("Running", Str(installedAndStarted.GetProperty("service"), "runState"));
        Assert.Equal(["start:RedisMCPSharp"], f.Host.Calls);
        Assert.Contains("Installed RedisMCPSharp 2.0.0", Str(installedAndStarted, "message"));
    }

    [Fact]
    public async Task Update_does_nothing_when_current_and_applies_a_newer_release_restarting_a_running_server()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.0.0");

        var notInstalled = await f.Call("update", """{ "service": "redis" }""");
        Assert.True(notInstalled.IsError);
        Assert.Contains("mcphub__install", Text(notInstalled));

        var unreachable = await f.Call("update", """{ "service": "kodi" }""");
        Assert.True(unreachable.IsError);
        Assert.Contains("Couldn't reach GitHub", Text(unreachable));
        Assert.Empty(f.Manager.Installs);

        f.Manager.Latest["KodiMCPSharp"] = Release("1.0.0");
        var current = Parse(await f.Call("update", """{ "service": "kodi" }"""));
        Assert.Contains("already up to date", Str(current, "message"));
        Assert.Empty(f.Manager.Installs);

        await f.Call("start", """{ "service": "kodi" }""");
        f.Host.Calls.Clear();
        f.Manager.Latest["KodiMCPSharp"] = Release("1.1.0");
        var updated = Parse(await f.Call("update", """{ "service": "kodi" }"""));

        Assert.Equal(["KodiMCPSharp:1.1.0"], f.Manager.Installs);
        Assert.Equal("1.0.0", Str(updated, "previousVersion"));
        Assert.True(updated.GetProperty("wasRunning").GetBoolean());
        Assert.Equal("1.1.0", Str(updated.GetProperty("service"), "installedVersion"));
        Assert.Equal("Running", Str(updated.GetProperty("service"), "runState"));
        Assert.Equal(["stop:KodiMCPSharp", "start:KodiMCPSharp"], f.Host.Calls);
        Assert.StartsWith("Updated KodiMCPSharp: 1.0.0 → 1.1.0", Str(updated, "message"));

        // force reinstalls the current release; restart=false leaves a running server down.
        f.Host.Calls.Clear();
        var reinstalled = Parse(await f.Call("update", """{ "service": "kodi", "force": true, "restart": false }"""));
        Assert.Equal(2, f.Manager.Installs.Count);
        Assert.StartsWith("Reinstalled", Str(reinstalled, "message"));
        Assert.Equal(["stop:KodiMCPSharp"], f.Host.Calls);
        Assert.Equal("Stopped", Str(reinstalled.GetProperty("service"), "runState"));
    }

    [Fact]
    public async Task Check_service_updates_covers_all_servers_or_one()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.0.0");
        f.Manager.Latest["KodiMCPSharp"] = Release("1.1.0");

        var all = Parse(await f.Call("check_service_updates"));
        Assert.Equal(2, all.GetProperty("checked").GetInt32());
        Assert.Equal(1, all.GetProperty("reachable").GetInt32());
        Assert.Equal(1, all.GetProperty("updatesAvailable").GetInt32());
        Assert.Contains("mcphub__update", Str(all, "note"));
        var kodi = all.GetProperty("services").EnumerateArray().Single(s => Str(s, "key") == "kodi");
        Assert.Equal("UpdateAvailable", Str(kodi, "updateStatus"));
        Assert.Equal("1.1.0", Str(kodi, "latestVersion"));

        var one = Parse(await f.Call("check_service_updates", """{ "service": "redis" }"""));
        Assert.Equal(1, one.GetProperty("checked").GetInt32());
        Assert.Equal(0, one.GetProperty("reachable").GetInt32());
        Assert.Contains("Couldn't reach GitHub", Str(one, "note"));
        Assert.Empty(f.Manager.Installs);
    }

    [Fact]
    public async Task Check_hub_update_compares_the_running_build_and_never_installs()
    {
        using var f = new Fixture();

        var unreachable = await f.Call("check_hub_update");
        Assert.True(unreachable.IsError);

        f.Releases.Releases["MCPHub"] = Release("99.0.0", "MCPHub-win-x64-v99.0.0.zip", "MCPHub-linux-x64-v99.0.0.zip");
        var json = Parse(await f.Call("check_hub_update"));

        Assert.Equal(HubRelease.CurrentVersion, Str(json, "currentVersion"));
        Assert.Equal("99.0.0", Str(json, "latestVersion"));
        Assert.Equal("UpdateAvailable", Str(json, "updateStatus"));
        Assert.Equal("2026-08-01", Str(json, "publishedAt"));
        Assert.EndsWith("/releases/latest", Str(json, "releasePageUrl"));
        Assert.Contains("MCPHub-", Str(json, "assetName"));
        Assert.Contains("does not update itself", Str(json, "note"));

        f.Releases.Releases["MCPHub"] = Release("0.0.1");
        var upToDate = Parse(await f.Call("check_hub_update"));
        Assert.Equal("UpToDate", Str(upToDate, "updateStatus"));
        Assert.False(upToDate.TryGetProperty("assetName", out _));

        f.Releases.Throws = new GithubAuthException("401");
        var rejected = await f.Call("check_hub_update");
        Assert.True(rejected.IsError);
        Assert.Contains("401", Text(rejected));
        Assert.Contains("GitHub token", Text(rejected));
    }

    [Fact]
    public async Task Overlapping_operations_on_one_server_are_refused_but_other_servers_proceed()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.0.0");
        FakeServiceManager.MarkInstalled(f.Redis, "1.0.0");
        f.Host.BlockStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = f.Call("start", """{ "service": "kodi" }""");
        Assert.False(first.IsCompleted);

        var overlapping = await f.Call("restart", """{ "service": "kodi" }""");
        Assert.True(overlapping.IsError);
        Assert.Contains("still in progress", Text(overlapping));

        var listing = await f.Call("list_services");
        Assert.NotEqual(true, listing.IsError);

        f.Host.BlockStart.SetResult();
        var completed = await first;
        Assert.NotEqual(true, completed.IsError);
        Assert.Equal(ServiceRunState.Running, f.Kodi.RunState);

        f.Host.BlockStart = null;
        var other = await f.Call("start", """{ "service": "redis" }""");
        Assert.NotEqual(true, other.IsError);
    }

    [Fact]
    public async Task Bad_arguments_and_unknown_tools_are_plain_errors()
    {
        using var f = new Fixture();
        FakeServiceManager.MarkInstalled(f.Kodi, "1.0.0");

        var badWait = await f.Call("start", """{ "service": "kodi", "wait_seconds": "soon" }""");
        Assert.True(badWait.IsError);
        Assert.Contains("wait_seconds", Text(badWait));

        var badBool = await f.Call("install", """{ "service": "redis", "start": "sometimes" }""");
        Assert.True(badBool.IsError);
        Assert.Contains("'start' must be true or false", Text(badBool));

        var bogus = await f.Call("explode");
        Assert.True(bogus.IsError);
        Assert.Empty(f.Host.Calls);
    }
}
