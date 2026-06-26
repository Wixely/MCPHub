namespace MCPHub.Core.Services.Github;

/// <summary>A single downloadable asset attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>The latest release of a product, distilled from the GitHub API.</summary>
/// <param name="Version">Tag without the leading <c>v</c>, e.g. <c>1.0.2</c>.</param>
/// <param name="TagName">Raw tag, e.g. <c>v1.0.2</c>.</param>
public sealed record ReleaseInfo(
    string Version,
    string TagName,
    DateTimeOffset? PublishedAt,
    bool IsPrerelease,
    IReadOnlyList<ReleaseAsset> Assets);
