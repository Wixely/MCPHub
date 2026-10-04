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

    /// <summary>Creates the authenticator from a token → tenant-id map.</summary>
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

/// <summary>
/// Resolves a token through a delegate — the bridge between the hosted endpoint and whatever holds the
/// hub's principals.
///
/// <para>A delegate rather than an interface so that this assembly needs no reference to the one that
/// owns the policy: the permissions document lives in MCPHub.Core, which already depends on the proxy,
/// and making the host depend on it as well would be a loop in the dependency graph drawn only to
/// carry one method. The composition root supplies <c>key =&gt; store.Resolve(key)</c>.</para>
/// </summary>
public sealed class DelegatingTenantAuthenticator : ITenantAuthenticator
{
    private readonly Func<string, TenantContext?> _resolve;

    /// <param name="resolve">Maps a presented token to a tenant, or null to refuse it. Called on the
    /// request path, so it should be a lookup rather than any kind of I/O.</param>
    public DelegatingTenantAuthenticator(Func<string, TenantContext?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    /// <inheritdoc />
    public ValueTask<TenantContext?> AuthenticateAsync(string token, CancellationToken cancellationToken)
        => ValueTask.FromResult(_resolve(token));
}
