using System.Net.Http.Headers;
using MCPHub.Core.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MockUpstream = MCPHub.Tests.RouterListenerTests.MockUpstream;
using RouterFixture = MCPHub.Tests.RouterListenerTests.RouterFixture;

namespace MCPHub.Tests;

/// <summary>Per-agent "last connected" tracking on the Router.</summary>
public sealed class RouterActivityTests
{
    [Fact]
    public void An_agent_that_has_never_connected_reports_no_activity()
    {
        using var fixture = new RouterFixture();
        Assert.False(fixture.Activity.Get("never-seen").HasConnected);
        Assert.Equal(0, fixture.Activity.Get("never-seen").RequestCount);
        Assert.Null(fixture.Activity.LastRejection);
    }

    [Fact]
    public async Task A_routed_request_stamps_the_agent_and_an_unknown_key_is_recorded_separately()
    {
        using var fixture = new RouterFixture();
        await using var upstream = await MockUpstream.StartAsync();
        var output = fixture.Store.SaveOutput(null, "Mock", upstream.Url, null, null);
        fixture.Store.SetDefault(output);
        var agent = fixture.Store.AddInput("Agent", null);
        var other = fixture.Store.AddInput("Quiet agent", null);

        await using var host = new RouterHost(fixture.Store, NullLogger<RouterHost>.Instance, activity: fixture.Activity);
        await host.StartAsync(portOverride: 0);

        var before = DateTimeOffset.UtcNow;
        await CallAsync(host, agent.Key);
        await CallAsync(host, agent.Key);

        var seen = fixture.Activity.Get(fixture.Store.Resolve(agent.Key)!.InputId);
        Assert.True(seen.HasConnected);
        Assert.Equal(2, seen.RequestCount);
        Assert.InRange(seen.LastConnected, before, DateTimeOffset.UtcNow);

        // An agent that has not called is not credited with someone else's traffic.
        Assert.False(fixture.Activity.Get(fixture.Store.Resolve(other.Key)!.InputId).HasConnected);

        Assert.Null(fixture.Activity.LastRejection);
        await CallAsync(host, "mhrouter_not_a_real_key_at_all_0000");
        Assert.NotNull(fixture.Activity.LastRejection);
    }

    [Fact]
    public async Task A_rejected_request_is_not_credited_to_any_agent()
    {
        using var fixture = new RouterFixture();
        var agent = fixture.Store.AddInput("Agent", null);
        var id = fixture.Store.Resolve(agent.Key)!.InputId;
        fixture.Store.SaveInput(id, "Agent", null, enabled: false);

        await using var host = new RouterHost(fixture.Store, NullLogger<RouterHost>.Instance, activity: fixture.Activity);
        await host.StartAsync(portOverride: 0);
        await CallAsync(host, agent.Key);

        // A disabled agent's key no longer resolves, so there is no agent to stamp — only the rejection.
        Assert.False(fixture.Activity.Get(id).HasConnected);
        Assert.NotNull(fixture.Activity.LastRejection);
    }

    [Fact]
    public void Activity_survives_a_restart_and_a_removed_agent_leaves_no_history_behind()
    {
        using var fixture = new RouterFixture();
        fixture.Activity.RecordConnection("agent-one");
        fixture.Activity.RecordConnection("agent-one");
        fixture.Activity.RecordConnection("agent-two");
        fixture.Activity.Flush();

        var reloaded = new RouterActivityLog(fixture, writeInterval: TimeSpan.Zero);
        Assert.Equal(2, reloaded.Get("agent-one").RequestCount);
        Assert.Equal(1, reloaded.Get("agent-two").RequestCount);

        reloaded.Forget("agent-one");
        reloaded.Flush();
        Assert.False(new RouterActivityLog(fixture, writeInterval: TimeSpan.Zero).Get("agent-one").HasConnected);
        Assert.True(new RouterActivityLog(fixture, writeInterval: TimeSpan.Zero).Get("agent-two").HasConnected);
    }

    [Fact]
    public async Task Activity_records_no_prompt_url_or_model_detail()
    {
        using var fixture = new RouterFixture();
        await using var upstream = await MockUpstream.StartAsync(context =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("""{"object":"list","data":[{"id":"secret-model-name"}]}""");
        });
        var output = fixture.Store.SaveOutput(null, "Mock", upstream.Url, "secret-model-name", "secret-upstream-key");
        fixture.Store.SetDefault(output);
        var agent = fixture.Store.AddInput("Agent", null);

        await using var host = new RouterHost(fixture.Store, NullLogger<RouterHost>.Instance, activity: fixture.Activity);
        await host.StartAsync(portOverride: 0);
        await CallAsync(host, agent.Key);
        fixture.Activity.Flush();

        var json = File.ReadAllText(Path.Combine(fixture.SettingsDirectory, RouterActivityLog.FileName));
        Assert.DoesNotContain("secret-model-name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-upstream-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain(agent.Key, json, StringComparison.Ordinal);
        Assert.DoesNotContain(upstream.Url, json, StringComparison.Ordinal);
    }

    private static async Task CallAsync(RouterHost host, string key)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/v1/"), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.GetAsync("models");
    }
}
