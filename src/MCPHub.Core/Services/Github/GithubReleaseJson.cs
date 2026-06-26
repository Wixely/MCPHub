using System.Text.Json.Serialization;

namespace MCPHub.Core.Services.Github;

// DTOs mirroring the subset of the GitHub "releases/latest" payload MCPHub needs.

internal sealed class GithubReleaseDto
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GithubAssetDto>? Assets { get; set; }
}

internal sealed class GithubAssetDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

/// <summary>Source-generated (trim/AOT-friendly) JSON context for the GitHub release payload.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GithubReleaseDto))]
internal sealed partial class GithubJsonContext : JsonSerializerContext;
