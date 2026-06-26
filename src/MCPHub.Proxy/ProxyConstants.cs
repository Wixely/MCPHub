namespace MCPHub.Proxy;

/// <summary>
/// Shared constants for the MCP aggregator. The connection pool, namespaced catalog and routing
/// handlers are implemented in milestone M4.
/// </summary>
public static class ProxyConstants
{
    /// <summary>
    /// Separator placed between a service key and an upstream tool/prompt name when re-exposing it,
    /// e.g. <c>noteworthy__list_notes</c>.
    /// </summary>
    public const string NamespaceSeparator = "__";

    /// <summary>URI scheme used when rewriting upstream resource URIs, e.g. <c>mcphub://noteworthy/…</c>.</summary>
    public const string ResourceScheme = "mcphub";
}
