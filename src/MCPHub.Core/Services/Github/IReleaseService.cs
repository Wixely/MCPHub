using MCPHub.Core.Catalog;

namespace MCPHub.Core.Services.Github;

/// <summary>Queries GitHub Releases for the Wixely products.</summary>
public interface IReleaseService
{
    /// <summary>
    /// Fetches the latest release for a product, or <see langword="null"/> when there are no releases
    /// or the request failed (network/HTTP/JSON errors are swallowed and logged, not thrown).
    /// Throws <see cref="GithubAuthException"/> when GitHub rejects the configured token (HTTP 401).
    /// </summary>
    Task<ReleaseInfo?> GetLatestReleaseAsync(ServiceCatalogEntry entry, CancellationToken cancellationToken = default);
}
