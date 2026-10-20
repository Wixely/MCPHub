using MCPHub.Hosting;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;
using static MCPHub.Tests.ProxyTestKit;

namespace MCPHub.Tests;

/// <summary>
/// A refresh exists for servers MCPHub does not run: when one of those changes its tools, nothing tells
/// the proxy. These tests stand up a real HTTP upstream whose tool list can be changed under the
/// registry and prove a reconnect re-reads it.
/// </summary>
public class UpstreamReconnectTests
{
    private static ProxyHost HostOf(FakeRegistry served) => new(new ProxyHandlers(served), NullLoggerFactory.Instance);

    private static string Exposed(string key, string tool) => key + ProxyConstants.NamespaceSeparator + tool;

    [Fact]
    public async Task Reconnect_picks_up_tools_the_upstream_added_since_it_connected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var served = RegistryWith(("svc", "echo"));
        var host = HostOf(served);
        await host.StartAsync("127.0.0.1", port: 0, cts.Token);
        var registry = new UpstreamRegistry(NullLoggerFactory.Instance);

        try
        {
            await registry.ConnectAsync("ext", "External", new Uri(host.EndpointUrl), cancellationToken: cts.Token);
            Assert.Equal([Exposed("ext", "svc__echo")], registry.Catalog.Tools.Select(t => t.Name));

            // The server "updates": it now offers a second tool. The registry has no way to know.
            served.Catalog = RegistryWith(("svc", "echo"), ("svc", "boom")).Catalog;
            Assert.Single(registry.Catalog.Tools);

            var changed = 0;
            registry.CatalogChanged += () => Interlocked.Increment(ref changed);
            var toolsBefore = Assert.Single(registry.Upstreams).Tools;

            Assert.True(await registry.ReconnectAsync("ext", cts.Token));

            var upstream = Assert.Single(registry.Upstreams);
            Assert.Equal(UpstreamState.Connected, upstream.State);
            Assert.Equal(2, upstream.ToolCount);
            Assert.Null(upstream.LastError);
            Assert.Equal(
                [Exposed("ext", "svc__boom"), Exposed("ext", "svc__echo")],
                registry.Catalog.Tools.Select(t => t.Name).Order());
            Assert.True(changed > 0, "a reconnect must announce the rebuilt catalog so the UI redraws");

            // The raw list is replaced, not mutated, so the one held from before the reconnect is still the old
            // one — that is what the Proxy page compares to show what an update changed.
            Assert.Equal(["svc__echo"], toolsBefore.Select(t => t.Name));
            Assert.Equal(["svc__boom", "svc__echo"], upstream.Tools.Select(t => t.Name).Order());
            var diff = ToolCatalogDiff.Compare(toolsBefore, upstream.Tools);
            Assert.Equal("svc__boom", Assert.Single(diff.Changes, c => c.Kind == ToolChangeKind.Added).Name);

            // The route points at the new session, and that session answers.
            var route = registry.Catalog.Routes[Exposed("ext", "svc__echo")];
            var result = await route.Client.ListToolsAsync(new ListToolsRequestParams(), cts.Token);
            Assert.Equal(2, result.Tools.Count);
        }
        finally
        {
            await registry.DisconnectAllAsync();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Reconnect_all_reads_every_upstream_again()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var servedA = RegistryWith(("a", "one"));
        var servedB = RegistryWith(("b", "one"));
        var hostA = HostOf(servedA);
        var hostB = HostOf(servedB);
        await hostA.StartAsync("127.0.0.1", port: 0, cts.Token);
        await hostB.StartAsync("127.0.0.1", port: 0, cts.Token);
        var registry = new UpstreamRegistry(NullLoggerFactory.Instance);

        try
        {
            await registry.ConnectAsync("first", "First", new Uri(hostA.EndpointUrl), cancellationToken: cts.Token);
            await registry.ConnectAsync("second", "Second", new Uri(hostB.EndpointUrl), cancellationToken: cts.Token);
            Assert.Equal(2, registry.Catalog.Tools.Count);

            servedA.Catalog = RegistryWith(("a", "one"), ("a", "two")).Catalog;
            servedB.Catalog = AggregatedCatalog.Empty;

            await registry.ReconnectAllAsync(cts.Token);

            Assert.All(registry.Upstreams, u => Assert.Equal(UpstreamState.Connected, u.State));
            Assert.Equal(
                [Exposed("first", "a__one"), Exposed("first", "a__two")],
                registry.Catalog.Tools.Select(t => t.Name).Order());
        }
        finally
        {
            await registry.DisconnectAllAsync();
            await hostA.StopAsync();
            await hostB.StopAsync();
        }
    }

    [Fact]
    public async Task Reconnect_of_an_unknown_key_is_reported_not_thrown()
    {
        var registry = new UpstreamRegistry(NullLoggerFactory.Instance);
        Assert.False(await registry.ReconnectAsync("nobody"));
        Assert.Empty(registry.Upstreams);
    }

    [Fact]
    public async Task Reconnect_of_a_faulted_upstream_retries_at_once_and_reports_the_fault()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var registry = new UpstreamRegistry(NullLoggerFactory.Instance);

        try
        {
            // Nothing listens here: the connect faults and the registry schedules a backed-off retry.
            await registry.ConnectAsync("dead", "Dead", new Uri("http://127.0.0.1:1/mcp"), cancellationToken: cts.Token);
            var before = Assert.Single(registry.Upstreams);
            Assert.Equal(UpstreamState.Faulted, before.State);

            Assert.True(await registry.ReconnectAsync("dead", cts.Token));

            var after = Assert.Single(registry.Upstreams);
            Assert.Equal(UpstreamState.Faulted, after.State);
            Assert.False(string.IsNullOrEmpty(after.LastError));
            Assert.Empty(registry.Catalog.Tools);
        }
        finally
        {
            await registry.DisconnectAllAsync();
        }
    }
}
