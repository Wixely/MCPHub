namespace MCPHub.Proxy;

/// <summary>
/// Identifies the caller a proxy request is served on behalf of. In multi-tenant embeddings
/// (e.g. one aggregated endpoint shared by many agent accounts) each agent maps to one tenant;
/// authorization and auditing key off <see cref="TenantId"/>.
/// </summary>
public sealed record TenantContext
{
    /// <summary>Tenant id used by <see cref="TenantContext.Default"/>.</summary>
    public const string DefaultTenantId = "default";

    /// <summary>
    /// The single-user tenant: the desktop app and any anonymous embedding run as this tenant,
    /// preserving the original "every caller sees everything" behavior when paired with
    /// <see cref="AllowAllToolAuthorization"/>.
    /// </summary>
    public static readonly TenantContext Default = new(DefaultTenantId);

    public TenantContext(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        TenantId = tenantId;
    }

    /// <summary>Stable identifier for the tenant (e.g. an agent account id).</summary>
    public string TenantId { get; }

    /// <summary>Whether this is the single-user default tenant.</summary>
    public bool IsDefault => string.Equals(TenantId, DefaultTenantId, StringComparison.Ordinal);
}
