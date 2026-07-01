namespace MCPHub.Core.Services.Github;

/// <summary>
/// Thrown when GitHub rejects the configured token (HTTP 401 Unauthorized). Kept distinct from
/// connectivity and rate-limit failures so the UI can tell the user to clear or replace the token
/// rather than mislabelling it as "couldn't reach GitHub".
/// </summary>
public sealed class GithubAuthException : Exception
{
    public GithubAuthException(string message) : base(message)
    {
    }
}
