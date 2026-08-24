namespace MCPHub.Proxy;

/// <summary>
/// Decides, per tenant, which aggregated tools are visible and callable. The proxy is the
/// enforcement point: an ungranted tool is filtered out of <c>tools/list</c> (not merely
/// call-blocked), so a tenant cannot even discover tools it has no grant for.
/// </summary>
public interface IToolAuthorization
{
    /// <summary>
    /// Whether <paramref name="tenant"/> may see <paramref name="exposedToolName"/> (the
    /// namespaced name, e.g. <c>noteworthy__list_notes</c>) owned by the upstream with
    /// <paramref name="serverKey"/> in <c>tools/list</c> results.
    /// </summary>
    bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName);

    /// <summary>
    /// Whether <paramref name="tenant"/> may call <paramref name="exposedToolName"/>. Implementations
    /// normally keep this consistent with <see cref="IsToolVisible"/>; the proxy checks both on every call.
    /// </summary>
    bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName);
}

/// <summary>
/// Allow-everything authorization: every tenant sees and may call every aggregated tool.
/// This is the default, preserving the original single-user behavior.
/// </summary>
public sealed class AllowAllToolAuthorization : IToolAuthorization
{
    /// <summary>Shared instance.</summary>
    public static readonly AllowAllToolAuthorization Instance = new();

    /// <inheritdoc />
    public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName) => true;

    /// <inheritdoc />
    public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName) => true;
}
