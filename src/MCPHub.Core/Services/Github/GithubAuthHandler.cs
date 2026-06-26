using System.Net.Http;
using System.Net.Http.Headers;
using MCPHub.Core.Settings;

namespace MCPHub.Core.Services.Github;

/// <summary>
/// Adds a bearer token to GitHub API requests when a PAT is available (from the secret store, or the
/// <c>MCPHUB_GITHUB_PAT</c> environment variable), lifting the unauthenticated 60-requests/hour limit.
/// Read per request so a PAT entered at runtime takes effect immediately.
/// </summary>
public sealed class GithubAuthHandler : DelegatingHandler
{
    private readonly ISecretStore _secretStore;

    public GithubAuthHandler(ISecretStore secretStore) => _secretStore = secretStore;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            var pat = _secretStore.Get(SecretKeys.GithubPat) ?? Environment.GetEnvironmentVariable("MCPHUB_GITHUB_PAT");
            if (!string.IsNullOrWhiteSpace(pat))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
