using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace MCPHub.Tests;

public class ProxyHandlersTenancyTests
{
    private static readonly TenantContext Alice = new("alice");
    private static readonly TenantContext Bob = new("bob");

    // ---- helpers -------------------------------------------------------------------------------

    private sealed class FakeRegistry : IUpstreamRegistry
    {
        public AggregatedCatalog Catalog { get; set; } = AggregatedCatalog.Empty;

        public IReadOnlyCollection<UpstreamServer> Upstreams => [];

        public event Action? CatalogChanged { add { } remove { } }

        public Task ConnectAsync(string key, string displayName, Uri endpoint, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ConnectStdioAsync(string key, string displayName, string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DisconnectAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAllAsync() => Task.CompletedTask;
    }

    private sealed class CollectingAuditSink : IProxyAuditSink
    {
        private readonly List<ToolCallAuditEvent> _events = [];

        public IReadOnlyList<ToolCallAuditEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public void Record(ToolCallAuditEvent auditEvent)
        {
            lock (_events) _events.Add(auditEvent);
        }
    }

    /// <summary>Builds a catalog of routed tools; routes carry a null client (fine unless a call is forwarded).</summary>
    private static FakeRegistry RegistryWith(params (string ServerKey, string ToolName)[] tools)
        => RegistryWith(client: null, tools);

    private static FakeRegistry RegistryWith(McpClient? client, params (string ServerKey, string ToolName)[] tools)
    {
        var advertised = new List<Tool>();
        var routes = new Dictionary<string, ToolRoute>(StringComparer.Ordinal);
        foreach (var (serverKey, toolName) in tools)
        {
            var exposed = serverKey + ProxyConstants.NamespaceSeparator + toolName;
            advertised.Add(new Tool { Name = exposed });
            routes[exposed] = new ToolRoute(client!, toolName, serverKey);
        }

        return new FakeRegistry { Catalog = new AggregatedCatalog(advertised, routes) };
    }

    private static StaticToolAuthorization Grants(params (string Tenant, string[] Patterns)[] grants)
        => new(new StaticToolAuthorizationOptions
        {
            Grants = grants.ToDictionary(g => g.Tenant, g => (IReadOnlyList<string>)g.Patterns),
        });

    private static string ExpectedDigest(IDictionary<string, JsonElement>? arguments)
    {
        var json = arguments is null ? "{}" : JsonSerializer.Serialize(arguments);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    private static string Text(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    // ---- listing -------------------------------------------------------------------------------

    [Fact]
    public void Default_construction_preserves_allow_all_single_user_behavior()
    {
        var registry = RegistryWith(("noteworthy", "list_notes"), ("azuredevops", "azdo_get_project"));
        var handlers = new ProxyHandlers(registry);

        var result = handlers.ListTools(TenantContext.Default);

        Assert.Equal(2, result.Tools.Count);
    }

    [Fact]
    public void Two_tenants_see_disjoint_catalogs_from_one_registry()
    {
        var registry = RegistryWith(("noteworthy", "list_notes"), ("azuredevops", "azdo_get_project"));
        var handlers = new ProxyHandlers(registry,
            Grants(("alice", ["noteworthy"]), ("bob", ["azuredevops"])));

        var aliceTools = handlers.ListTools(Alice).Tools.Select(t => t.Name).ToList();
        var bobTools = handlers.ListTools(Bob).Tools.Select(t => t.Name).ToList();

        Assert.Equal(["noteworthy__list_notes"], aliceTools);
        Assert.Equal(["azuredevops__azdo_get_project"], bobTools);
        Assert.Empty(aliceTools.Intersect(bobTools));
    }

    // ---- call authorization --------------------------------------------------------------------

    [Fact]
    public async Task Denied_call_returns_mcp_error_and_audits_denied_with_digest_only()
    {
        var registry = RegistryWith(("noteworthy", "delete_note"));
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(registry, Grants(("alice", ["somethingelse"])), sink);

        var arguments = Args(("note", "top-secret-content"));
        var result = await handlers.CallToolAsync(Alice,
            new CallToolRequestParams { Name = "noteworthy__delete_note", Arguments = arguments },
            CancellationToken.None);

        Assert.True(result.IsError);

        var evt = Assert.Single(sink.Events);
        Assert.Equal(ToolCallOutcome.Denied, evt.Outcome);
        Assert.Equal("alice", evt.TenantId);
        Assert.Equal("noteworthy__delete_note", evt.Tool);
        Assert.Equal(ExpectedDigest(arguments), evt.ArgumentsSha256);
        Assert.Matches("^[0-9a-f]{64}$", evt.ArgumentsSha256);
        Assert.DoesNotContain("top-secret-content", evt.ArgumentsSha256);
    }

    [Fact]
    public async Task Denied_call_is_indistinguishable_from_unknown_tool()
    {
        var registry = RegistryWith(("noteworthy", "delete_note"));
        var handlers = new ProxyHandlers(registry, Grants());

        var denied = await handlers.CallToolAsync(Alice,
            new CallToolRequestParams { Name = "noteworthy__delete_note" }, CancellationToken.None);
        var unknown = await handlers.CallToolAsync(Alice,
            new CallToolRequestParams { Name = "noteworthy__no_such_tool" }, CancellationToken.None);

        var deniedText = Text(denied).Replace("noteworthy__delete_note", "{tool}");
        var unknownText = Text(unknown).Replace("noteworthy__no_such_tool", "{tool}");
        Assert.Equal(unknownText, deniedText);
    }

    [Fact]
    public async Task Visible_but_call_blocked_tool_is_denied()
    {
        var registry = RegistryWith(("noteworthy", "delete_note"));
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(registry, new VisibleButNotCallable(), sink);

        var result = await handlers.CallToolAsync(Alice,
            new CallToolRequestParams { Name = "noteworthy__delete_note" }, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ToolCallOutcome.Denied, Assert.Single(sink.Events).Outcome);
    }

    private sealed class VisibleButNotCallable : IToolAuthorization
    {
        public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName) => true;
        public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName) => false;
    }

    [Fact]
    public async Task Unknown_tool_audits_error_outcome()
    {
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(new FakeRegistry(), null, sink);

        var result = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "gone__tool" }, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ToolCallOutcome.Error, Assert.Single(sink.Events).Outcome);
    }

    [Fact]
    public async Task Missing_tool_name_is_an_error_without_an_audit_event()
    {
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(new FakeRegistry(), null, sink);

        var result = await handlers.CallToolAsync(TenantContext.Default, null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task Digest_is_deterministic_and_argument_sensitive()
    {
        var registry = RegistryWith(("noteworthy", "delete_note"));
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(registry, Grants(), sink);

        var argsA = Args(("note", "one"));
        await handlers.CallToolAsync(Alice, new CallToolRequestParams { Name = "noteworthy__delete_note", Arguments = argsA }, CancellationToken.None);
        await handlers.CallToolAsync(Alice, new CallToolRequestParams { Name = "noteworthy__delete_note", Arguments = Args(("note", "one")) }, CancellationToken.None);
        await handlers.CallToolAsync(Alice, new CallToolRequestParams { Name = "noteworthy__delete_note", Arguments = Args(("note", "two")) }, CancellationToken.None);

        var digests = sink.Events.Select(e => e.ArgumentsSha256).ToList();
        Assert.Equal(digests[0], digests[1]);
        Assert.NotEqual(digests[0], digests[2]);
    }

    // ---- forwarding through a real in-process upstream ----------------------------------------

    private static async Task<McpClient> StartInProcessUpstreamAsync(CancellationToken cancellationToken)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(),
                "fake-upstream", NullLoggerFactory.Instance),
            new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "fake-upstream", Version = "1.0" },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (context, ct) => ValueTask.FromResult(new ListToolsResult
                    {
                        Tools =
                        [
                            new Tool { Name = "echo", Description = "Echoes the msg argument." },
                            new Tool { Name = "boom", Description = "Always reports a tool error." },
                        ],
                    }),
                    CallToolHandler = (context, ct) => ValueTask.FromResult(context.Params?.Name switch
                    {
                        "echo" => new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = "echo:" + context.Params.Arguments?["msg"].GetString() }],
                        },
                        "boom" => new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = "boom failed" }],
                        },
                        _ => throw new InvalidOperationException("unexpected tool"),
                    }),
                },
            },
            NullLoggerFactory.Instance,
            serviceProvider: null);

        _ = server.RunAsync(cancellationToken);

        return await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                NullLoggerFactory.Instance),
            clientOptions: null,
            NullLoggerFactory.Instance,
            cancellationToken);
    }

    [Fact]
    public async Task Granted_call_forwards_upstream_and_audits_success_with_digest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await StartInProcessUpstreamAsync(cts.Token);

        var registry = RegistryWith(client, ("svc", "echo"));
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(registry, Grants(("alice", ["svc"])), sink);

        var arguments = Args(("msg", "hello"));
        var result = await handlers.CallToolAsync(Alice,
            new CallToolRequestParams { Name = "svc__echo", Arguments = arguments }, cts.Token);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("echo:hello", Text(result));

        var evt = Assert.Single(sink.Events);
        Assert.Equal(ToolCallOutcome.Success, evt.Outcome);
        Assert.Equal(ExpectedDigest(arguments), evt.ArgumentsSha256);
        Assert.DoesNotContain("hello", evt.ArgumentsSha256);
        Assert.True(evt.Duration >= TimeSpan.Zero);
        Assert.True(evt.TimestampUtc <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Upstream_tool_error_result_audits_error_outcome()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await StartInProcessUpstreamAsync(cts.Token);

        var registry = RegistryWith(client, ("svc", "boom"));
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(registry, null, sink);

        var result = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "svc__boom" }, cts.Token);

        Assert.True(result.IsError);
        Assert.Equal(ToolCallOutcome.Error, Assert.Single(sink.Events).Outcome);
    }
}
