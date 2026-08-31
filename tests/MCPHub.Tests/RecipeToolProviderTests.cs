using System.Text.Json;
using MCPHub.Core.Recipes;
using MCPHub.Hosting;
using MCPHub.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using static MCPHub.Tests.ProxyTestKit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

public class RecipeToolProviderTests
{
    private static (RecipeStore Store, RecipeToolProvider Provider) NewProvider(TempDir dir)
    {
        var store = new RecipeStore(new FakeAppPaths(dir.Path), NullLogger<RecipeStore>.Instance);
        return (store, new RecipeToolProvider(store));
    }

    private static Dictionary<string, JsonElement> JsonArgs(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static JsonElement Parse(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement;

    [Fact]
    public void Advertises_the_five_recipe_tools_with_object_schemas()
    {
        using var dir = new TempDir();
        var (_, provider) = NewProvider(dir);

        Assert.Equal("recipes", provider.Key);
        Assert.Equal(["list", "get", "add", "update", "remove"], provider.Tools.Select(t => t.Name));
        Assert.All(provider.Tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.Equal("object", t.InputSchema.GetProperty("type").GetString());
        });
    }

    [Fact]
    public async Task Add_then_list_get_update_remove_roundtrip()
    {
        using var dir = new TempDir();
        var (store, provider) = NewProvider(dir);

        var added = await provider.CallAsync("add", JsonArgs("""
            {
              "title": "Access Kodi",
              "when": "kodi tools fail because Kodi is not running",
              "then": "Launch Kodi with adb__launch_app, then retry.",
              "services": ["Kodi", "ADB"],
              "notes": "Give it a few seconds."
            }
            """), CancellationToken.None);

        Assert.NotEqual(true, added.IsError);
        var addedJson = Parse(added);
        var id = addedJson.GetProperty("id").GetString()!;
        Assert.Matches("^[0-9a-f]{8}$", id);
        Assert.Equal("agent", addedJson.GetProperty("source").GetString());
        Assert.Equal(["kodi", "adb"], addedJson.GetProperty("services").EnumerateArray().Select(e => e.GetString()));

        var listed = Parse(await provider.CallAsync("list", null, CancellationToken.None));
        Assert.Equal(1, listed.GetProperty("count").GetInt32());
        Assert.Equal(id, listed.GetProperty("recipes")[0].GetProperty("id").GetString());

        var got = Parse(await provider.CallAsync("get", JsonArgs($$"""{ "id": "{{id}}" }"""), CancellationToken.None));
        Assert.Equal("Access Kodi", got.GetProperty("title").GetString());

        // Partial update: only 'then' and 'notes' change; a blank notes string clears them.
        var updated = Parse(await provider.CallAsync("update", JsonArgs($$"""
            { "id": "{{id}}", "then": "Use adb__launch_app.", "notes": "" }
            """), CancellationToken.None));
        Assert.Equal("Use adb__launch_app.", updated.GetProperty("then").GetString());
        Assert.Equal("Access Kodi", updated.GetProperty("title").GetString());
        Assert.False(updated.TryGetProperty("notes", out _), "cleared notes are omitted from the JSON");
        Assert.Null(store.Find(id)!.Notes);

        var removed = await provider.CallAsync("remove", JsonArgs($$"""{ "id": "{{id}}" }"""), CancellationToken.None);
        Assert.NotEqual(true, removed.IsError);
        Assert.Contains(id, Text(removed));
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task List_filters_by_query_and_service()
    {
        using var dir = new TempDir();
        var (store, provider) = NewProvider(dir);
        store.Add(new RecipeDraft { Title = "Access Kodi", When = "w", Then = "t", Services = ["kodi", "adb"] }, RecipeSources.User);
        store.Add(new RecipeDraft { Title = "Paperless consume", When = "w", Then = "t", Services = ["paperlessngx"] }, RecipeSources.User);

        var byService = Parse(await provider.CallAsync("list", JsonArgs("""{ "service": "ADB" }"""), CancellationToken.None));
        Assert.Equal(1, byService.GetProperty("count").GetInt32());
        Assert.Equal("Access Kodi", byService.GetProperty("recipes")[0].GetProperty("title").GetString());

        var byQuery = Parse(await provider.CallAsync("list", JsonArgs("""{ "query": "paperless" }"""), CancellationToken.None));
        Assert.Equal(1, byQuery.GetProperty("count").GetInt32());

        var none = Parse(await provider.CallAsync("list", JsonArgs("""{ "query": "paperless", "service": "kodi" }"""), CancellationToken.None));
        Assert.Equal(0, none.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Services_may_be_given_as_a_comma_separated_string()
    {
        using var dir = new TempDir();
        var (_, provider) = NewProvider(dir);

        var added = Parse(await provider.CallAsync("add", JsonArgs("""
            { "title": "t", "when": "w", "then": "x", "services": "kodi, adb ,remoteadmin" }
            """), CancellationToken.None));

        Assert.Equal(["kodi", "adb", "remoteadmin"], added.GetProperty("services").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Validation_failures_are_tool_errors_with_a_plain_message()
    {
        using var dir = new TempDir();
        var (store, provider) = NewProvider(dir);

        var missing = await provider.CallAsync("add", JsonArgs("""{ "title": "t", "when": "w" }"""), CancellationToken.None);
        Assert.True(missing.IsError);
        Assert.Equal("'then' is required.", Text(missing));

        var badType = await provider.CallAsync("add", JsonArgs("""{ "title": "t", "when": "w", "then": "x", "services": 42 }"""), CancellationToken.None);
        Assert.True(badType.IsError);
        Assert.Contains("'services' must be an array of strings", Text(badType));

        var noId = await provider.CallAsync("remove", null, CancellationToken.None);
        Assert.True(noId.IsError);
        Assert.Equal("'id' is required.", Text(noId));

        Assert.Empty(store.All);
    }

    [Fact]
    public async Task Unknown_id_and_unknown_tool_are_errors()
    {
        using var dir = new TempDir();
        var (_, provider) = NewProvider(dir);

        var get = await provider.CallAsync("get", JsonArgs("""{ "id": "deadbeef" }"""), CancellationToken.None);
        Assert.True(get.IsError);
        Assert.Contains("deadbeef", Text(get));
        Assert.Contains("recipes__list", Text(get));

        var update = await provider.CallAsync("update", JsonArgs("""{ "id": "deadbeef", "title": "x" }"""), CancellationToken.None);
        Assert.True(update.IsError);

        var remove = await provider.CallAsync("remove", JsonArgs("""{ "id": "deadbeef" }"""), CancellationToken.None);
        Assert.True(remove.IsError);

        var bogus = await provider.CallAsync("explode", null, CancellationToken.None);
        Assert.True(bogus.IsError);
    }

    // ---- through the proxy -----------------------------------------------------------------------

    [Fact]
    public void Proxy_advertises_recipe_tools_namespaced_beside_upstreams()
    {
        using var dir = new TempDir();
        var (_, provider) = NewProvider(dir);
        var handlers = new ProxyHandlers(RegistryWith(("kodi", "kodi_list_instances")), null, null, null, [provider]);

        var tools = handlers.ListTools(TenantContext.Default).Tools;

        Assert.Equal(["kodi__kodi_list_instances", "recipes__list", "recipes__get", "recipes__add", "recipes__update", "recipes__remove"],
            tools.Select(t => t.Name));
        var add = tools.Single(t => t.Name == "recipes__add");
        Assert.StartsWith("[Recipes] ", add.Description);
        Assert.Equal("object", add.InputSchema.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Proxy_routes_recipe_calls_and_audits_them()
    {
        using var dir = new TempDir();
        var (store, provider) = NewProvider(dir);
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(new FakeRegistry(), null, sink, null, [provider]);

        var arguments = JsonArgs("""{ "title": "Access Kodi", "when": "w", "then": "t" }""");
        var result = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "recipes__add", Arguments = arguments }, CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Access Kodi", Assert.Single(store.All).Title);

        var evt = Assert.Single(sink.Events);
        Assert.Equal("recipes__add", evt.Tool);
        Assert.Equal(ToolCallOutcome.Success, evt.Outcome);
        Assert.Equal(ExpectedDigest(arguments), evt.ArgumentsSha256);

        var bad = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "recipes__get", Arguments = JsonArgs("""{ "id": "nope" }""") }, CancellationToken.None);
        Assert.True(bad.IsError);
        Assert.Equal(ToolCallOutcome.Error, sink.Events[^1].Outcome);
    }

    [Fact]
    public async Task Tenant_without_a_recipes_grant_cannot_see_or_call_them()
    {
        using var dir = new TempDir();
        var (store, provider) = NewProvider(dir);
        var sink = new CollectingAuditSink();
        var handlers = new ProxyHandlers(RegistryWith(("kodi", "kodi_list_instances")),
            Grants(("alice", ["kodi"]), ("bob", ["recipes"])), sink, null, [provider]);

        Assert.Equal(["kodi__kodi_list_instances"], handlers.ListTools(new TenantContext("alice")).Tools.Select(t => t.Name));
        Assert.Equal(5, handlers.ListTools(new TenantContext("bob")).Tools.Count);

        var denied = await handlers.CallToolAsync(new TenantContext("alice"),
            new CallToolRequestParams { Name = "recipes__add", Arguments = JsonArgs("""{ "title": "t", "when": "w", "then": "x" }""") },
            CancellationToken.None);

        Assert.True(denied.IsError);
        Assert.Contains("Unknown tool", Text(denied));
        Assert.Equal(ToolCallOutcome.Denied, Assert.Single(sink.Events).Outcome);
        Assert.Empty(store.All);
    }

    [Fact]
    public void Duplicate_local_tool_names_are_rejected_at_construction()
    {
        using var dir = new TempDir();
        var (_, provider) = NewProvider(dir);

        Assert.Throws<ArgumentException>(() => new ProxyHandlers(new FakeRegistry(), null, null, null, [provider, provider]));
    }

    [Fact]
    public void Container_registration_used_by_the_app_wires_local_providers()
    {
        // Mirrors Composition.cs exactly. Registering ProxyHandlers by type is NOT enough: the container
        // falls back to the registry-only constructor (the policy overload has a non-defaulted parameter
        // it cannot resolve) and the providers are silently dropped — hence the explicit factory.
        using var dir = new TempDir();
        var (_, provider) = NewProvider(dir);

        using var services = new ServiceCollection()
            .AddSingleton<IUpstreamRegistry>(new FakeRegistry())
            .AddSingleton<ILocalToolProvider>(provider)
            .AddSingleton(sp => new ProxyHandlers(
                sp.GetRequiredService<IUpstreamRegistry>(),
                authorization: null,
                auditSink: null,
                tenantResolver: null,
                localToolProviders: sp.GetServices<ILocalToolProvider>()))
            .BuildServiceProvider();

        var handlers = services.GetRequiredService<ProxyHandlers>();

        Assert.Contains("recipes__add", handlers.ListTools(TenantContext.Default).Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Host_serves_recipes_and_instructions_over_http()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var dir = new TempDir();
        var (store, provider) = NewProvider(dir);

        var host = new ProxyHost(
            new ProxyHandlers(new FakeRegistry(), null, null, null, [provider]),
            NullLoggerFactory.Instance,
            new ProxyHostOptions { ServerInstructions = RecipeToolProvider.ServerInstructions });
        await host.StartAsync("127.0.0.1", port: 0, cts.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(host.EndpointUrl),
                    TransportMode = HttpTransportMode.StreamableHttp,
                }, NullLoggerFactory.Instance),
                clientOptions: null, NullLoggerFactory.Instance, cts.Token);

            Assert.Equal(RecipeToolProvider.ServerInstructions, client.ServerInstructions);

            var tools = await client.ListToolsAsync(new ListToolsRequestParams(), cts.Token);
            Assert.Contains("recipes__list", tools.Tools.Select(t => t.Name));

            var added = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "recipes__add",
                Arguments = JsonArgs("""{ "title": "Access Kodi", "when": "Kodi is not running", "then": "Launch it with adb__launch_app.", "services": ["kodi", "adb"] }"""),
            }, cts.Token);
            Assert.NotEqual(true, added.IsError);
            var id = Parse(added).GetProperty("id").GetString();

            Assert.Equal(id, Assert.Single(store.All).Id);
            Assert.True(File.Exists(store.FilePath));
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
