using MCPHub.Proxy;

namespace MCPHub.Hosting;

/// <summary>
/// Maps a bearer token presented to the hosted endpoint to a <see cref="TenantContext"/>.
/// Supplied via <see cref="ProxyHostOptions.TenantAuthenticator"/>; when present, every HTTP
/// request must carry a token this authenticator accepts.
/// </summary>
public interface ITenantAuthenticator
{
    /// <summary>
    /// Resolves <paramref name="token"/> (the value after <c>Bearer </c>, never null or empty) to a
    /// tenant, or <see langword="null"/> to reject the request.
    /// </summary>
    ValueTask<TenantContext?> AuthenticateAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// Fixed token → tenant map from a plain options object — the static counterpart of
/// <see cref="StaticToolAuthorization"/>. Token comparison is ordinal.
/// </summary>
public sealed class StaticTenantAuthenticator : ITenantAuthenticator
{
    private readonly Dictionary<string, TenantContext> _tokens;

    public StaticTenantAuthenticator(IReadOnlyDictionary<string, string> tokenToTenantId)
    {
        ArgumentNullException.ThrowIfNull(tokenToTenantId);
        _tokens = tokenToTenantId.ToDictionary(
            pair => pair.Key,
            pair => new TenantContext(pair.Value),
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<TenantContext?> AuthenticateAsync(string token, CancellationToken cancellationToken)
        => ValueTask.FromResult(_tokens.TryGetValue(token, out var tenant) ? tenant : null);
}
