using System.Security.Claims;

namespace MCPHub.Proxy;

/// <summary>Claim types the proxy understands.</summary>
public static class ProxyClaimTypes
{
    /// <summary>Claim carrying the tenant id an authenticated caller was resolved to.</summary>
    public const string TenantId = "mcphub:tenant";
}

/// <summary>
/// Maps the authenticated principal of an incoming MCP message to a <see cref="TenantContext"/>.
/// The hosting layer authenticates transport credentials (e.g. a bearer token) and stamps the
/// principal; the proxy only ever sees the resolved tenant.
/// </summary>
public interface ITenantResolver
{
    /// <summary>Resolves the tenant for a request. Never returns <see langword="null"/>.</summary>
    TenantContext Resolve(ClaimsPrincipal? user);
}

/// <summary>Resolves every caller to <see cref="TenantContext.Default"/> (single-user behavior).</summary>
public sealed class DefaultTenantResolver : ITenantResolver
{
    /// <summary>Shared instance.</summary>
    public static readonly DefaultTenantResolver Instance = new();

    /// <inheritdoc />
    public TenantContext Resolve(ClaimsPrincipal? user) => TenantContext.Default;
}

/// <summary>
/// Resolves the tenant from the principal's <see cref="ProxyClaimTypes.TenantId"/> claim, falling
/// back to <see cref="TenantContext.Default"/> when the claim is absent (anonymous mode).
/// </summary>
public sealed class ClaimsTenantResolver : ITenantResolver
{
    /// <summary>Shared instance.</summary>
    public static readonly ClaimsTenantResolver Instance = new();

    /// <inheritdoc />
    public TenantContext Resolve(ClaimsPrincipal? user)
    {
        var tenantId = user?.FindFirst(ProxyClaimTypes.TenantId)?.Value;
        return string.IsNullOrWhiteSpace(tenantId) ? TenantContext.Default : new TenantContext(tenantId);
    }
}
