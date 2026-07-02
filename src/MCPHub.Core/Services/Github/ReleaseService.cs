using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using MCPHub.Core.Catalog;
using MCPHub.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Services.Github;

/// <summary>
/// <see cref="IReleaseService"/> backed by the GitHub REST API. Resolves a named <see cref="HttpClient"/>
/// ("github") per request (configured with the required User-Agent and an optional bearer token).
/// Caches ETags and issues conditional requests so unchanged releases return 304 — which is fast and
/// does not count against the GitHub rate limit. Rate-limit (403) responses are handled gracefully.
/// </summary>
public sealed class ReleaseService : IReleaseService
{
    public const string HttpClientName = "github";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ReleaseService> _logger;
    private readonly ConcurrentDictionary<string, CachedRelease> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _cachePath;

    public ReleaseService(IHttpClientFactory httpClientFactory, IAppPaths appPaths, ILogger<ReleaseService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cachePath = Path.Combine(appPaths.DataDirectory, "release-cache.json");
        LoadCache();
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(ServiceCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        var url = entry.LatestReleaseApiUrl;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_cache.TryGetValue(url, out var cached))
            request.Headers.IfNoneMatch.Add(cached.ETag);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotModified && _cache.TryGetValue(url, out var unchanged))
                return unchanged.Info;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("No releases published for {Service}.", entry.Name);
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && IsRateLimited(response))
            {
                _logger.LogWarning("GitHub rate limit hit while checking {Service}{Reset}. Set a PAT in Settings to raise the limit.",
                    entry.Name, FormatReset(response));
                return _cache.TryGetValue(url, out var stale) ? stale.Info : null;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("GitHub rejected the configured token (401) while checking {Service}.", entry.Name);
                throw new GithubAuthException("GitHub rejected the configured token (401 Unauthorized).");
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var dto = await JsonSerializer.DeserializeAsync(stream, GithubJsonContext.Default.GithubReleaseDto, cancellationToken);

            if (dto?.TagName is null)
            {
                _logger.LogWarning("Latest release for {Service} had no tag_name.", entry.Name);
                return null;
            }

            var assets = (dto.Assets ?? [])
                .Where(a => a is { Name: not null, BrowserDownloadUrl: not null })
                .Select(a => new ReleaseAsset(a.Name!, a.BrowserDownloadUrl!, a.Size))
                .ToList();

            var info = new ReleaseInfo(
                Version: dto.TagName.TrimStart('v', 'V'),
                TagName: dto.TagName,
                PublishedAt: dto.PublishedAt,
                IsPrerelease: dto.Prerelease,
                Assets: assets);

            if (response.Headers.ETag is { } etag)
            {
                _cache[url] = new CachedRelease(etag, info);
                await SaveCacheAsync();
            }

            return info;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // genuine caller cancellation
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to fetch latest release for {Service}.", entry.Name);
            return null;
        }
    }

    public ReleaseInfo? GetCachedRelease(ServiceCatalogEntry entry)
        => _cache.TryGetValue(entry.LatestReleaseApiUrl, out var cached) ? cached.Info : null;

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;

            var json = File.ReadAllText(_cachePath);
            var file = JsonSerializer.Deserialize(json, ReleaseCacheJsonContext.Default.ReleaseCacheFile);
            if (file is null)
                return;

            foreach (var (url, entry) in file.Entries)
            {
                if (entry.Info is not null
                    && EntityTagHeaderValue.TryParse(entry.ETag, out var etag)
                    && etag is not null)
                {
                    _cache[url] = new CachedRelease(etag, entry.Info);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException)
        {
            _logger.LogWarning(ex, "Could not read the release cache; starting empty.");
        }
    }

    private async Task SaveCacheAsync()
    {
        var file = new ReleaseCacheFile();
        foreach (var (url, cached) in _cache)
            file.Entries[url] = new ReleaseCacheEntry { ETag = cached.ETag.ToString(), Info = cached.Info };

        await _saveGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = JsonSerializer.Serialize(file, ReleaseCacheJsonContext.Default.ReleaseCacheFile);
            var tmp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist the release cache.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response)
        => response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
           && values.FirstOrDefault() == "0";

    private static string FormatReset(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var unix))
        {
            return $" (resets {DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime():HH:mm})";
        }

        return string.Empty;
    }

    private sealed record CachedRelease(EntityTagHeaderValue ETag, ReleaseInfo Info);
}
