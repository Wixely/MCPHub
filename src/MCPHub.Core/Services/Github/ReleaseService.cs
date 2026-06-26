using System.Net;
using System.Net.Http;
using System.Text.Json;
using MCPHub.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Services.Github;

/// <summary>
/// <see cref="IReleaseService"/> backed by the GitHub REST API. Resolves a named
/// <see cref="HttpClient"/> ("github") from the factory per request — the client is configured by the
/// composition root with the required User-Agent and an optional bearer token for higher rate limits.
/// </summary>
public sealed class ReleaseService : IReleaseService
{
    /// <summary>Name of the configured <see cref="HttpClient"/> this service resolves.</summary>
    public const string HttpClientName = "github";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ReleaseService> _logger;

    public ReleaseService(IHttpClientFactory httpClientFactory, ILogger<ReleaseService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(ServiceCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await http.GetAsync(
                entry.LatestReleaseApiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("No releases published for {Service}.", entry.Name);
                return null;
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var dto = await JsonSerializer.DeserializeAsync(
                stream, GithubJsonContext.Default.GithubReleaseDto, cancellationToken);

            if (dto?.TagName is null)
            {
                _logger.LogWarning("Latest release for {Service} had no tag_name.", entry.Name);
                return null;
            }

            var assets = (dto.Assets ?? [])
                .Where(a => a is { Name: not null, BrowserDownloadUrl: not null })
                .Select(a => new ReleaseAsset(a.Name!, a.BrowserDownloadUrl!, a.Size))
                .ToList();

            return new ReleaseInfo(
                Version: dto.TagName.TrimStart('v', 'V'),
                TagName: dto.TagName,
                PublishedAt: dto.PublishedAt,
                IsPrerelease: dto.Prerelease,
                Assets: assets);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // genuine caller cancellation — let it propagate
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // network failure, request timeout, or malformed payload — treat as "couldn't check"
            _logger.LogWarning(ex, "Failed to fetch latest release for {Service}.", entry.Name);
            return null;
        }
    }
}
