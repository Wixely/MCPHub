using MCPHub.Core.Routing;
using Microsoft.AspNetCore.Http;
using Xunit;
using MockUpstream = MCPHub.Tests.RouterListenerTests.MockUpstream;

namespace MCPHub.Tests;

/// <summary>The per-output "does this actually work" probe on the Router page.</summary>
public sealed class RouterOutputTesterTests
{
    [Fact]
    public async Task A_reachable_provider_passes_and_reports_how_many_models_it_offers()
    {
        await using var upstream = await MockUpstream.StartAsync("""{"object":"list","data":[{"id":"a"},{"id":"b"}]}""");
        using var tester = NewTester();

        var result = await tester.TestAsync(new RouterOutput { Name = "Local", BaseUrl = upstream.Url });

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ModelCount);
        Assert.Contains("2 model", result.Summary);
    }

    [Fact]
    public async Task A_model_override_the_provider_does_not_offer_fails_and_names_what_is_available()
    {
        await using var upstream = await MockUpstream.StartAsync("""{"object":"list","data":[{"id":"llama-3"},{"id":"qwen-3"}]}""");
        using var tester = NewTester();

        var found = await tester.TestAsync(new RouterOutput { Name = "Local", BaseUrl = upstream.Url, Model = "qwen-3" });
        Assert.True(found.Succeeded);
        Assert.True(found.OverrideModelFound);

        var missing = await tester.TestAsync(new RouterOutput { Name = "Local", BaseUrl = upstream.Url, Model = "gpt-9" });
        Assert.False(missing.Succeeded);
        Assert.False(missing.OverrideModelFound);
        Assert.Contains("gpt-9", missing.Summary);
        Assert.Contains("llama-3", missing.Summary);
    }

    [Fact]
    public async Task The_stored_upstream_key_is_the_one_presented_to_the_provider()
    {
        string? seen = null;
        await using var upstream = await MockUpstream.StartAsync(context =>
        {
            seen = context.Request.Headers.Authorization.ToString();
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("""{"object":"list","data":[]}""");
        });

        using var tester = new RouterOutputTester(readApiKey: _ => "upstream-secret");
        var result = await tester.TestAsync(new RouterOutput { Name = "Cloud", BaseUrl = upstream.Url });

        Assert.True(result.Succeeded);
        Assert.Equal("Bearer upstream-secret", seen);
    }

    [Theory]
    [InlineData(401, "rejected the stored upstream key")]
    [InlineData(404, "path prefix")]
    [InlineData(429, "rate-limiting")]
    public async Task Provider_errors_are_reported_as_what_to_change(int status, string expected)
    {
        await using var upstream = await MockUpstream.StartAsync(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });

        using var tester = new RouterOutputTester(readApiKey: _ => "a-key");
        var result = await tester.TestAsync(new RouterOutput { Name = "Cloud", BaseUrl = upstream.Url });

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unauthenticated_401_asks_for_a_key_rather_than_blaming_the_stored_one()
    {
        await using var upstream = await MockUpstream.StartAsync(context =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        });

        using var tester = NewTester();
        var result = await tester.TestAsync(new RouterOutput { Name = "Cloud", BaseUrl = upstream.Url });

        Assert.False(result.Succeeded);
        Assert.Contains("requires a key", result.Summary);
    }

    [Fact]
    public async Task Nothing_listening_and_a_bad_url_both_fail_without_throwing()
    {
        using var tester = NewTester();

        var refused = await tester.TestAsync(new RouterOutput { Name = "Dead", BaseUrl = "http://127.0.0.1:1/v1" });
        Assert.False(refused.Succeeded);
        Assert.Contains("Nothing is listening", refused.Summary);

        var malformed = await tester.TestAsync(new RouterOutput { Name = "Bad", BaseUrl = "not a url" });
        Assert.False(malformed.Succeeded);
        Assert.Contains("API base URL", malformed.Summary);
    }

    [Fact]
    public async Task A_redirect_is_reported_rather_than_followed_matching_how_the_router_forwards()
    {
        await using var upstream = await MockUpstream.StartAsync(context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = "https://elsewhere.example/v1/models";
            return Task.CompletedTask;
        });

        using var tester = NewTester();
        var result = await tester.TestAsync(new RouterOutput { Name = "Redirecting", BaseUrl = upstream.Url });

        Assert.False(result.Succeeded);
        Assert.Contains("redirected", result.Summary);
    }

    /// <summary>No credential and no DPAPI: these outputs are built in memory, never stored.</summary>
    private static RouterOutputTester NewTester() => new(readApiKey: _ => null);
}
