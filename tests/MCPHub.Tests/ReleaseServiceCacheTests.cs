using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MCPHub.Core.Catalog;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Services.Github;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

public sealed class ReleaseServiceCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcphub-relcache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Persisted_etag_drives_a_conditional_request_that_survives_a_restart()
    {
        var entry = ServiceCatalog.FindByName("NoteworthyMCPSharp")!;

        // First run — empty cache → unconditional request → 200 + ETag, and the cache is persisted.
        var run1 = new StubHandler(req =>
        {
            Assert.Empty(req.Headers.IfNoneMatch);
            return Ok("1.0.0", "\"etag-1\"");
        });
        var info1 = await new ReleaseService(Factory(run1), Paths(_dir), NullLogger<ReleaseService>.Instance)
            .GetLatestReleaseAsync(entry);

        Assert.Equal("1.0.0", info1!.Version);
        Assert.Equal(1, run1.Calls);
        Assert.True(File.Exists(Path.Combine(_dir, "release-cache.json")));

        // Second run — a *fresh* ReleaseService over the same cache dir (simulates an app restart).
        // It must send the persisted ETag and, on 304, return the cached version (no rate-limit cost).
        var run2 = new StubHandler(req =>
        {
            Assert.Equal("\"etag-1\"", req.Headers.IfNoneMatch.Single().Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var info2 = await new ReleaseService(Factory(run2), Paths(_dir), NullLogger<ReleaseService>.Instance)
            .GetLatestReleaseAsync(entry);

        Assert.Equal("1.0.0", info2!.Version);
    }

    [Fact]
    public async Task Rate_limited_after_restart_still_returns_the_cached_version()
    {
        var entry = ServiceCatalog.FindByName("NoteworthyMCPSharp")!;

        var seed = new StubHandler(_ => Ok("2.3.4", "\"etag-x\""));
        await new ReleaseService(Factory(seed), Paths(_dir), NullLogger<ReleaseService>.Instance)
            .GetLatestReleaseAsync(entry);

        // Fresh instance, GitHub now answers 403 with X-RateLimit-Remaining: 0.
        var limited = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
            resp.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return resp;
        });
        var info = await new ReleaseService(Factory(limited), Paths(_dir), NullLogger<ReleaseService>.Instance)
            .GetLatestReleaseAsync(entry);

        Assert.Equal("2.3.4", info!.Version); // served from the persisted cache instead of collapsing to "—"
    }

    private static HttpResponseMessage Ok(string version, string etag)
    {
        var body = "{\"tag_name\":\"v" + version + "\",\"prerelease\":false,\"assets\":[]}";
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        resp.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return resp;
    }

    private static IAppPaths Paths(string dir) => new TestPaths(dir);
    private static IHttpClientFactory Factory(HttpMessageHandler handler) => new TestFactory(handler);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TestFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("https://api.github.com/") };
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
