using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

public sealed class RouterTests
{
    [Fact]
    public void Routing_persists_and_global_changes_preserve_overrides()
    {
        using var fixture = new StoreFixture();
        var store = fixture.Store;
        Assert.False(store.Snapshot.StartOnLaunch);
        var first = store.SaveOutput(null, "First", "http://localhost:8000/v1", "model-one", "test-output-key");
        var second = store.SaveOutput(null, "Second", "http://localhost:8001/v1", null, null);
        var shared = store.AddInput("Default agent", null);
        var pinned = store.AddInput("Pinned agent", first);
        store.SetDefault(first);
        Assert.Equal(first, store.Resolve(shared.Key)!.Output!.Id);
        store.SetDefault(second);
        Assert.Equal(second, store.Resolve(shared.Key)!.Output!.Id);
        Assert.Equal(first, store.Resolve(pinned.Key)!.Output!.Id);
        store.Configure("0.0.0.0", 5805, true);
        var reloaded = new RouterStore(fixture);
        Assert.True(reloaded.Snapshot.StartOnLaunch);
        Assert.Equal(5805, reloaded.Snapshot.Port);
        Assert.Equal("0.0.0.0", reloaded.Snapshot.BindAddress);
        Assert.Equal(first, reloaded.Resolve(pinned.Key)!.Output!.Id);
        Assert.Equal("test-output-key", RouterStore.ReadApiKey(reloaded.Resolve(pinned.Key)!.Output!));
        var json = File.ReadAllText(Path.Combine(fixture.SettingsDirectory, "router.json"));
        Assert.DoesNotContain(shared.Key, json);
        Assert.DoesNotContain(pinned.Key, json);
        Assert.DoesNotContain("test-output-key", json);
    }

    [Fact]
    public void Keys_are_unique_rotatable_revocable_and_not_recoverable_from_snapshots()
    {
        using var f = new StoreFixture();
        var one = f.Store.AddInput("One", null);
        var two = f.Store.AddInput("Two", null);
        Assert.NotEqual(one.Key, two.Key);
        Assert.Null(f.Store.Resolve("invalid-key-that-does-not-exist"));
        var rotated = f.Store.RotateKey(one.Id);
        Assert.Null(f.Store.Resolve(one.Key));
        Assert.NotNull(f.Store.Resolve(rotated));
        f.Store.SaveInput(one.Id, "One", null, false);
        Assert.Null(f.Store.Resolve(rotated));
        f.Store.SaveInput(one.Id, "One", null, true);
        Assert.NotNull(f.Store.Resolve(rotated));
        f.Store.RemoveInput(one.Id);
        Assert.Null(f.Store.Resolve(rotated));
        Assert.NotNull(f.Store.Resolve(two.Key));
    }

    [Fact]
    public void Output_edit_preserves_or_explicitly_clears_credentials_and_references()
    {
        using var f = new StoreFixture();
        var id = f.Store.SaveOutput(null, "Output", "http://localhost:8000/v1", null, "upstream-key");
        f.Store.SaveOutput(id, "Renamed", "http://localhost:8001/v1", "new-model", null);
        Assert.Equal("upstream-key", RouterStore.ReadApiKey(Assert.Single(f.Store.Snapshot.Outputs)));
        f.Store.SetDefault(id);
        Assert.Throws<ArgumentException>(() => f.Store.RemoveOutput(id));
        f.Store.SetDefault(null);
        var input = f.Store.AddInput("Agent", id);
        Assert.Throws<ArgumentException>(() => f.Store.RemoveOutput(id));
        f.Store.SaveInput(input.Id, "Agent", null, true);
        f.Store.SaveOutput(id, "Output", "http://localhost:8001/v1", null, "");
        Assert.Null(RouterStore.ReadApiKey(Assert.Single(f.Store.Snapshot.Outputs)));
        f.Store.RemoveOutput(id);
        Assert.Empty(f.Store.Snapshot.Outputs);
    }

    [Theory]
    [InlineData("file:///tmp/model")]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://example.com/v1?key=bad")]
    [InlineData("https://example.com/v1#fragment")]
    [InlineData("")]
    public void Invalid_output_urls_do_not_mutate_configuration(string url)
    {
        using var f = new StoreFixture();
        Assert.Throws<ArgumentException>(() => f.Store.SaveOutput(null, "Bad", url, null, null));
        Assert.Empty(f.Store.Snapshot.Outputs);
    }

    [Fact]
    public void Failed_save_does_not_publish_new_routes()
    {
        using var f = new StoreFixture();
        var input = f.Store.AddInput("Keep", null);
        var path = Path.Combine(f.SettingsDirectory, "router.json");
        File.Move(path, path + ".backup");
        Directory.CreateDirectory(path);
        var error = Record.Exception(() => f.Store.RemoveInput(input.Id));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Directory.Delete(path);
        File.Move(path + ".backup", path);
        Assert.NotNull(f.Store.Resolve(input.Key));
        Assert.NotNull(new RouterStore(f).Resolve(input.Key));
        Assert.Empty(Directory.GetFiles(f.SettingsDirectory, "*.tmp"));
    }

    [Fact]
    public void Corrupt_configuration_is_not_overwritten_and_does_not_enable_access()
    {
        using var f = new StoreFixture();
        File.WriteAllText(Path.Combine(f.SettingsDirectory, "router.json"), "not-json");
        var store = new RouterStore(f);
        Assert.NotNull(store.LoadError);
        Assert.Throws<InvalidOperationException>(() => store.AddInput("Agent", null));
        Assert.Equal("not-json", File.ReadAllText(Path.Combine(f.SettingsDirectory, "router.json")));
    }

    [Fact]
    public async Task Authentication_and_endpoint_allowlist_fail_before_contacting_output()
    {
        using var f = new StoreFixture();
        var calls = 0;
        await using var upstream = await MockServer.Start(async c => { Interlocked.Increment(ref calls); await c.Response.WriteAsync("{}"); });
        var id = f.Store.SaveOutput(null, "Mock", upstream.Url + "/v1", null, null);
        f.Store.SetDefault(id);
        var input = f.Store.AddInput("Agent", null);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router);
        using var missing = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", input.Key);
        foreach (var path in new[] { "files", "../admin", "models?api_key=bad" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        f.Store.SaveInput(input.Id, "Agent", null, false);
        using var disabled = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("chat/completions")]
    [InlineData("responses")]
    [InlineData("completions")]
    [InlineData("embeddings")]
    public async Task Requests_preserve_payload_and_isolate_upstream_credentials(string path)
    {
        using var f = new StoreFixture();
        string? captured = null;
        string? authorization = null;
        string? requestPath = null;
        string? cookie = null;
        string? providerHeader = null;
        await using var upstream = await MockServer.Start(async c =>
        {
            captured = await new StreamReader(c.Request.Body).ReadToEndAsync();
            authorization = c.Request.Headers.Authorization.ToString();
            cookie = c.Request.Headers.Cookie.ToString();
            providerHeader = c.Request.Headers["OpenAI-Project"].ToString();
            requestPath = c.Request.Path;
            c.Response.StatusCode = 429;
            c.Response.Headers.RetryAfter = "7";
            c.Response.Headers.SetCookie = "private=value";
            await c.Response.WriteAsync("{\"error\":{\"message\":\"rate limited\"}}");
        });
        var output = f.Store.SaveOutput(null, "Mock", upstream.Url + "/api/v1", "override-model", "provider-secret");
        var input = f.Store.AddInput("Agent", output);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router, input.Key);
        client.DefaultRequestHeaders.Add("Cookie", "agent=private");
        client.DefaultRequestHeaders.Add("OpenAI-Project", "agent-project");
        using var response = await client.PostAsync(path, Json("{\"model\":\"old\",\"input\":\"hello\",\"stream\":true,\"tools\":[{\"type\":\"function\"}]}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("7", response.Headers.GetValues("Retry-After").Single());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("rate limited", await response.Content.ReadAsStringAsync());
        Assert.Equal("Bearer provider-secret", authorization);
        Assert.Equal(string.Empty, cookie);
        Assert.Equal(string.Empty, providerHeader);
        Assert.Equal("/api/v1/" + path, requestPath);
        using var body = JsonDocument.Parse(captured!);
        Assert.Equal("override-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", body.RootElement.GetProperty("input").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public async Task Global_and_input_route_updates_take_effect_without_restarting_listener()
    {
        using var f = new StoreFixture();
        await using var one = await MockServer.Start(c => c.Response.WriteAsync("one"));
        await using var two = await MockServer.Start(c => c.Response.WriteAsync("two"));
        var a = f.Store.SaveOutput(null, "One", one.Url + "/v1", null, null);
        var b = f.Store.SaveOutput(null, "Two", two.Url + "/v1", null, null);
        f.Store.SetDefault(a);
        var input = f.Store.AddInput("Default", null);
        var pinned = f.Store.AddInput("Pinned", a);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router, input.Key);
        using var pinnedClient = Client(router, pinned.Key);
        Assert.Equal("one", await client.GetStringAsync("models"));
        f.Store.SetDefault(b);
        Assert.Equal("two", await client.GetStringAsync("models"));
        Assert.Equal("one", await pinnedClient.GetStringAsync("models"));
        f.Store.SaveInput(pinned.Id, "Pinned", b, true);
        Assert.Equal("two", await pinnedClient.GetStringAsync("models"));
        f.Store.RotateKey(input.Id);
        using var revoked = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task Streaming_delivers_first_event_before_completion_and_keeps_captured_route()
    {
        using var f = new StoreFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await MockServer.Start(async c =>
        {
            c.Response.ContentType = "text/event-stream";
            await c.Response.WriteAsync("data: first\n\n");
            await c.Response.Body.FlushAsync();
            await release.Task.WaitAsync(c.RequestAborted);
            await c.Response.WriteAsync("data: [DONE]\n\n");
        });
        var output = f.Store.SaveOutput(null, "Stream", upstream.Url + "/v1", null, null);
        f.Store.SetDefault(output);
        var input = f.Store.AddInput("Agent", null);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router, input.Key);
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = Json("{\"model\":\"local\",\"stream\":true}") };
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            Assert.Equal("data: first", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            f.Store.SetDefault(null);
            release.TrySetResult();
            Assert.Contains("data: [DONE]", await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            using var unrouted = await client.GetAsync("models");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unrouted.StatusCode);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Client_disconnect_cancels_upstream_stream()
    {
        using var f = new StoreFixture();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await MockServer.Start(async c =>
        {
            c.Response.ContentType = "text/event-stream";
            await c.Response.WriteAsync("data: first\n\n");
            await c.Response.Body.FlushAsync();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, c.RequestAborted); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        });
        var output = f.Store.SaveOutput(null, "Stream", upstream.Url + "/v1", null, null);
        var input = f.Store.AddInput("Agent", output);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router, input.Key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses") { Content = Json("{\"stream\":true}") };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.Dispose();
        client.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Redirects_are_not_followed_and_input_keys_never_reach_unauthenticated_output()
    {
        using var f = new StoreFixture();
        string? auth = null;
        await using var upstream = await MockServer.Start(c =>
        {
            auth = c.Request.Headers.Authorization.ToString();
            c.Response.StatusCode = 307;
            c.Response.Headers.Location = "http://localhost:1/never";
            return Task.CompletedTask;
        });
        var output = f.Store.SaveOutput(null, "Redirect", upstream.Url + "/v1", null, null);
        var input = f.Store.AddInput("Agent", output);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router, input.Key);
        using var response = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("upstream_redirect", await response.Content.ReadAsStringAsync());
        Assert.Equal(string.Empty, auth);
    }

    [Theory]
    [InlineData("not json", "application/json", 400)]
    [InlineData("[]", "application/json", 400)]
    [InlineData("{}", "text/plain", 415)]
    [InlineData("{\"previous_response_id\":\"old\"}", "application/json", 400)]
    [InlineData("{\"background\":true}", "application/json", 400)]
    public async Task Invalid_or_stateful_requests_return_clear_errors(string body, string mediaType, int status)
    {
        using var f = new StoreFixture();
        var output = f.Store.SaveOutput(null, "Unused", "http://localhost:1/v1", null, null);
        var input = f.Store.AddInput("Agent", output);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router, input.Key);
        using var response = await client.PostAsync("responses", new StringContent(body, Encoding.UTF8, mediaType));
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Oversized_requests_are_rejected_and_router_can_restart()
    {
        using var f = new StoreFixture();
        var output = f.Store.SaveOutput(null, "Unused", "http://localhost:1/v1", null, null);
        var input = f.Store.AddInput("Agent", output);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router, input.Key);
        client.DefaultRequestHeaders.ExpectContinue = true;
        using var response = await client.PostAsync("chat/completions", Json(new string('x', RouterHost.MaxRequestBytes + 1)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await router.StopAsync();
        Assert.False(router.IsRunning);
        await router.StartAsync(0);
        Assert.True(router.IsRunning);
    }

    [Fact]
    public async Task Loopback_self_routes_are_rejected()
    {
        using var f = new StoreFixture();
        await using var router = await StartRouter(f.Store);
        var output = f.Store.SaveOutput(null, "Loop", router.EndpointUrl, null, null);
        var input = f.Store.AddInput("Agent", output);
        using var client = Client(router, input.Key);
        using var response = await client.GetAsync("models");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("routing_loop", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task No_model_override_preserves_original_json_bytes()
    {
        using var f = new StoreFixture();
        var original = "{ \"model\": \"agent-model\", \"messages\": [], \"custom\": {\"value\": 1.00} }";
        string? captured = null;
        await using var upstream = await MockServer.Start(async c =>
        {
            captured = await new StreamReader(c.Request.Body).ReadToEndAsync();
            await c.Response.WriteAsync("{}");
        });
        var output = f.Store.SaveOutput(null, "Mock", upstream.Url + "/v1", null, null);
        var input = f.Store.AddInput("Agent", output);
        await using var router = await StartRouter(f.Store);
        using var client = Client(router, input.Key);
        using var response = await client.PostAsync("chat/completions", Json(original));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original, captured);
    }

    [Fact]
    public async Task Failed_start_releases_resources_and_does_not_report_running()
    {
        using var f = new StoreFixture();
        await using var occupied = await MockServer.Start(c => c.Response.WriteAsync("occupied"));
        await using var router = new RouterHost(f.Store, NullLogger<RouterHost>.Instance);
        await Assert.ThrowsAnyAsync<IOException>(() => router.StartAsync(new Uri(occupied.Url).Port));
        Assert.False(router.IsRunning);
        await router.StartAsync(0);
        Assert.True(router.IsRunning);
    }

    [Fact]
    public async Task Starting_application_does_not_enable_router_by_default()
    {
        using var f = new StoreFixture();
        await using var router = new RouterHost(f.Store, NullLogger<RouterHost>.Instance);
        await router.StartConfiguredAsync();
        Assert.False(router.IsRunning);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
    private static HttpClient Client(RouterHost router, string? key = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(router.EndpointUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
        if (key is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return client;
    }
    private static async Task<RouterHost> StartRouter(RouterStore store)
    {
        var router = new RouterHost(store, NullLogger<RouterHost>.Instance);
        await router.StartAsync(0);
        return router;
    }
    private sealed class StoreFixture : IAppPaths, IDisposable
    {
        public string SettingsDirectory { get; } = Path.Combine(Path.GetTempPath(), "mcphub-router-tests", Guid.NewGuid().ToString("N"));
        public string DataDirectory => SettingsDirectory;
        public string DownloadsDirectory => SettingsDirectory;
        public string DefaultServersDirectory => SettingsDirectory;
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
        public RouterStore Store { get; }
        public StoreFixture() { Directory.CreateDirectory(SettingsDirectory); Store = new(this); }
        public void Dispose() => Directory.Delete(SettingsDirectory, recursive: true);
    }
    private sealed class MockServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public static async Task<MockServer> Start(RequestDelegate handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Run(handler);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, address);
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
