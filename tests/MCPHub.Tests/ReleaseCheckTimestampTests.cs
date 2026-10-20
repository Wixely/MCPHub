using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// The release cache records when GitHub last answered, which paces the automatic hub check. Only answers
/// count: a 200 and a 304 both move the clock, a rate-limit or outage does not, and a cache written before
/// the field existed reads as never checked.
/// </summary>
public sealed class ReleaseCheckTimestampTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcphub-reltime-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ReleaseService Service(Func<HttpRequestMessage, HttpResponseMessage> respond, DateTimeOffset now)
        => new(new StubFactory(new StubHandler(respond)), new TestPaths(_dir), NullLogger<ReleaseService>.Instance, new FixedTime(now));

    [Fact]
    public async Task A_cache_file_from_before_the_field_existed_reads_as_never_checked()
    {
        // The exact shape ReleaseService wrote before CheckedAt was added.
        var url = HubRelease.Catalog.LatestReleaseApiUrl;
        var json = "{\"Entries\":{\"" + url + "\":{\"ETag\":\"\\\"etag-old\\\"\",\"Info\":{\"Version\":\"0.9.0\",\"TagName\":\"v0.9.0\",\"PublishedAt\":null,\"IsPrerelease\":false,\"Assets\":[]}}}}";
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "release-cache.json"), json);

        var service = Service(_ => throw new InvalidOperationException("no request expected"), T0);

        Assert.Equal("0.9.0", service.GetCachedRelease(HubRelease.Catalog)?.Version);
        Assert.Null(service.GetLastCheckedAt(HubRelease.Catalog));
    }

    [Fact]
    public async Task A_fresh_release_stamps_the_check_time_and_it_survives_a_restart()
    {
        await Service(_ => Ok("1.0.0", "\"e1\""), T0).GetLatestReleaseAsync(HubRelease.Catalog);

        var reloaded = Service(_ => throw new InvalidOperationException("no request expected"), T0.AddDays(1));

        Assert.Equal(T0, reloaded.GetLastCheckedAt(HubRelease.Catalog));
    }

    [Fact]
    public async Task Not_modified_is_still_a_check_and_moves_the_clock()
    {
        await Service(_ => Ok("1.0.0", "\"e1\""), T0).GetLatestReleaseAsync(HubRelease.Catalog);

        var later = Service(_ => new HttpResponseMessage(HttpStatusCode.NotModified), T0.AddDays(2));
        var info = await later.GetLatestReleaseAsync(HubRelease.Catalog);

        Assert.Equal("1.0.0", info?.Version);
        Assert.Equal(T0.AddDays(2), later.GetLastCheckedAt(HubRelease.Catalog));

        // And persisted, so the next launch does not re-check.
        Assert.Equal(T0.AddDays(2), Service(_ => throw new InvalidOperationException(), T0.AddDays(3)).GetLastCheckedAt(HubRelease.Catalog));
    }

    [Fact]
    public async Task A_rate_limited_answer_does_not_count_as_a_check()
    {
        await Service(_ => Ok("1.0.0", "\"e1\""), T0).GetLatestReleaseAsync(HubRelease.Catalog);

        var limited = Service(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
            resp.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return resp;
        }, T0.AddDays(2));
        var info = await limited.GetLatestReleaseAsync(HubRelease.Catalog);

        Assert.Equal("1.0.0", info?.Version); // the stale answer is still served
        Assert.Equal(T0, limited.GetLastCheckedAt(HubRelease.Catalog)); // but the clock did not move
    }

    private static HttpResponseMessage Ok(string version, string etag)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tag_name\":\"v" + version + "\",\"prerelease\":false,\"assets\":[]}", Encoding.UTF8, "application/json"),
        };
        resp.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return resp;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.github.com/") };
    }

    private sealed class TestPaths(string dir) : IAppPaths
    {
        public string SettingsDirectory => dir;
        public string DataDirectory => dir;
        public string DownloadsDirectory => dir;
        public string DefaultServersDirectory => dir;
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
    }
}
