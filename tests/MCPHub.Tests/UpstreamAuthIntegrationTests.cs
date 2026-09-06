using MCPHub.Core.Settings;
using MCPHub.Hosting;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static MCPHub.Tests.ProxyTestKit;

namespace MCPHub.Tests;

/// <summary>
/// Proves a token configured on a user-added server actually reaches the upstream, over real HTTP.
/// A bearer-mode <see cref="ProxyHost"/> stands in for any MCP server that refuses unauthenticated
/// callers, so "the credential is presented" is asserted by the connection succeeding at all.
/// </summary>
public class UpstreamAuthIntegrationTests
{
    private const string Token = "tok_upstream_secret";

    private static async Task<ProxyHost> StartAuthenticatingServerAsync(CancellationToken cancellationToken)
    {
        var upstream = await StartInProcessUpstreamAsync(cancellationToken);
        var handlers = new ProxyHandlers(
            RegistryWith(upstream, ("svc", "echo")),
            Grants(("caller", ["svc"])),
            auditSink: null,
            tenantResolver: ClaimsTenantResolver.Instance);

        var host = new ProxyHost(handlers, NullLoggerFactory.Instance, new ProxyHostOptions
        {
            TenantAuthenticator = new StaticTenantAuthenticator(new Dictionary<string, string> { [Token] = "caller" }),
        });

        await host.StartAsync("127.0.0.1", port: 0, cancellationToken);
        return host;
    }

    [Fact]
    public async Task Bearer_token_gets_an_upstream_that_requires_auth_connected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var host = await StartAuthenticatingServerAsync(cts.Token);
        var registry = new UpstreamRegistry(NullLoggerFactory.Instance);

        try
        {
            var definition = new UserMcpServerDefinition
            {
                DisplayName = "Secured remote",
                Kind = McpTransportKind.Http,
                Endpoint = host.EndpointUrl,
                Auth = McpAuthKind.BearerToken,
            };

            await registry.ConnectAsync(
                definition.Key, definition.DisplayName, new Uri(host.EndpointUrl),
                UserServerAuth.BuildHeaders(definition, Token), cts.Token);

            var connected = Assert.Single(registry.Upstreams);
            Assert.Equal(UpstreamState.Connected, connected.State);
            Assert.Null(connected.LastError);

            // Its tools are namespaced into the aggregated catalog, which is the point of connecting.
            Assert.Contains(registry.Catalog.Tools, t => t.Name == definition.Key + ProxyConstants.NamespaceSeparator + "svc__echo");
        }
        finally
        {
            await registry.DisconnectAllAsync();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task The_same_upstream_faults_without_the_token()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var host = await StartAuthenticatingServerAsync(cts.Token);
        var registry = new UpstreamRegistry(NullLoggerFactory.Instance);

        try
        {
            await registry.ConnectAsync("user-secured", "Secured remote", new Uri(host.EndpointUrl), headers: null, cts.Token);

            var faulted = Assert.Single(registry.Upstreams);
            Assert.Equal(UpstreamState.Faulted, faulted.State);
            Assert.Empty(registry.Catalog.Tools);
        }
        finally
        {
            await registry.DisconnectAllAsync();
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_token_in_the_url_is_kept_out_of_the_endpoint_label()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var registry = new UpstreamRegistry(NullLoggerFactory.Instance);

        try
        {
            // Nothing is listening; the connection faults, which is the state whose label is most often read.
            var withCredentials = new Uri("http://tok_secret@127.0.0.1:1/mcp");
            await registry.ConnectAsync("user-pasted", "Pasted URL", withCredentials, headers: null, cts.Token);

            var upstream = Assert.Single(registry.Upstreams);
            Assert.DoesNotContain("tok_secret", upstream.Endpoint, StringComparison.Ordinal);
            Assert.Equal("http://127.0.0.1:1/mcp", upstream.Endpoint);
        }
        finally
        {
            await registry.DisconnectAllAsync();
        }
    }
}
