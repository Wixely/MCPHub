using MCPHub.Proxy;
using ModelContextProtocol.Protocol;
using Xunit;
using static MCPHub.Tests.ProxyTestKit;

namespace MCPHub.Tests;

public class ProxyHandlersTenancyTests
{
    private static readonly TenantContext Alice = new("alice");
    private static readonly TenantContext Bob = new("bob");

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

        await handlers.CallToolAsync(Alice, new CallToolRequestParams { Name = "noteworthy__delete_note", Arguments = Args(("note", "one")) }, CancellationToken.None);
        await handlers.CallToolAsync(Alice, new CallToolRequestParams { Name = "noteworthy__delete_note", Arguments = Args(("note", "one")) }, CancellationToken.None);
        await handlers.CallToolAsync(Alice, new CallToolRequestParams { Name = "noteworthy__delete_note", Arguments = Args(("note", "two")) }, CancellationToken.None);

        var digests = sink.Events.Select(e => e.ArgumentsSha256).ToList();
        Assert.Equal(digests[0], digests[1]);
        Assert.NotEqual(digests[0], digests[2]);
    }

    // ---- forwarding through a real in-process upstream ----------------------------------------

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
