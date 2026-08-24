using MCPHub.Hosting;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using static MCPHub.Tests.ProxyTestKit;

namespace MCPHub.Tests;

/// <summary>
/// End-to-end coverage of <see cref="ProxyHost"/> over real HTTP on a loopback port the OS picks
/// (port 0 — never the desktop app's 5800, so a live MCPHub instance is left alone). These tests
/// also pin the tenant-flow contract: the ASP.NET principal stamped by bearer authentication must
/// reach <c>RequestContext.User</c> inside the MCP handlers.
/// </summary>
public class ProxyHostIntegrationTests
{
    private static async Task<McpClient> ConnectAsync(ProxyHost host, string? token, CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(host.EndpointUrl),
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        if (token is not null)
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };

        return await McpClient.CreateAsync(
            new HttpClientTransport(options, NullLoggerFactory.Instance),
            clientOptions: null, NullLoggerFactory.Instance, cancellationToken);
    }

    private static async Task<List<string>> ListToolNamesAsync(McpClient client, CancellationToken cancellationToken)
    {
        var result = await client.ListToolsAsync(new ListToolsRequestParams(), cancellationToken);
        return result.Tools.Select(t => t.Name).ToList();
    }

    [Fact]
    public async Task Anonymous_host_preserves_single_user_behavior()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var upstream = await StartInProcessUpstreamAsync(cts.Token);
        var registry = RegistryWith(upstream, ("svc", "echo"), ("svc", "boom"));

        var host = new ProxyHost(new ProxyHandlers(registry), NullLoggerFactory.Instance);
        await host.StartAsync("127.0.0.1", port: 0, cts.Token);
        try
        {
            Assert.NotEqual(0, host.Port);
            await using var client = await ConnectAsync(host, token: null, cts.Token);

            Assert.Equal(["svc__echo", "svc__boom"], await ListToolNamesAsync(client, cts.Token));

            var call = await client.CallToolAsync(
                new CallToolRequestParams { Name = "svc__echo", Arguments = Args(("msg", "hi")) }, cts.Token);
            Assert.Equal("echo:hi", Text(call));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Bearer_mode_binds_tenants_per_request_and_rejects_anonymous_callers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var upstream = await StartInProcessUpstreamAsync(cts.Token);
        var registry = RegistryWith(upstream, ("svc1", "echo"), ("svc2", "echo"));

        var handlers = new ProxyHandlers(
            registry,
            Grants(("alice", ["svc1"]), ("bob", ["svc2"])),
            auditSink: null,
            tenantResolver: ClaimsTenantResolver.Instance);

        var host = new ProxyHost(handlers, NullLoggerFactory.Instance, new ProxyHostOptions
        {
            TenantAuthenticator = new StaticTenantAuthenticator(new Dictionary<string, string>
            {
                ["token-a"] = "alice",
                ["token-b"] = "bob",
            }),
        });

        await host.StartAsync("127.0.0.1", port: 0, cts.Token);
        try
        {
            await using var alice = await ConnectAsync(host, "token-a", cts.Token);
            await using var bob = await ConnectAsync(host, "token-b", cts.Token);

            // Discovery-level filtering: one endpoint, disjoint catalogs.
            Assert.Equal(["svc1__echo"], await ListToolNamesAsync(alice, cts.Token));
            Assert.Equal(["svc2__echo"], await ListToolNamesAsync(bob, cts.Token));

            // A granted call succeeds; an ungranted one comes back as an MCP error result.
            var granted = await alice.CallToolAsync(
                new CallToolRequestParams { Name = "svc1__echo", Arguments = Args(("msg", "hi")) }, cts.Token);
            Assert.Equal("echo:hi", Text(granted));

            var denied = await alice.CallToolAsync(
                new CallToolRequestParams { Name = "svc2__echo", Arguments = Args(("msg", "hi")) }, cts.Token);
            Assert.True(denied.IsError);

            // No token (or a bad one) → the transport is refused outright.
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var anonymous = await ConnectAsync(host, token: null, cts.Token);
            });
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await using var wrong = await ConnectAsync(host, "not-a-token", cts.Token);
            });
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Two_fixed_tenant_hosts_run_side_by_side_in_one_process()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var upstream = await StartInProcessUpstreamAsync(cts.Token);
        var registry = RegistryWith(upstream, ("svc1", "echo"), ("svc2", "echo"));
        var grants = Grants(("alice", ["svc1"]), ("bob", ["svc2"]));

        var hostA = new ProxyHost(
            new ProxyHandlers(registry, grants, null, new FixedTenantResolver(new TenantContext("alice"))),
            NullLoggerFactory.Instance);
        var hostB = new ProxyHost(
            new ProxyHandlers(registry, grants, null, new FixedTenantResolver(new TenantContext("bob"))),
            NullLoggerFactory.Instance);

        await hostA.StartAsync("127.0.0.1", port: 0, cts.Token);
        await hostB.StartAsync("127.0.0.1", port: 0, cts.Token);
        try
        {
            Assert.NotEqual(hostA.Port, hostB.Port);

            await using var clientA = await ConnectAsync(hostA, token: null, cts.Token);
            await using var clientB = await ConnectAsync(hostB, token: null, cts.Token);

            Assert.Equal(["svc1__echo"], await ListToolNamesAsync(clientA, cts.Token));
            Assert.Equal(["svc2__echo"], await ListToolNamesAsync(clientB, cts.Token));
        }
        finally
        {
            await hostA.StopAsync();
            await hostB.StopAsync();
        }
    }
}
