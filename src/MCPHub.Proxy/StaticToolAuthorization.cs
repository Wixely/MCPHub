using System.Text.RegularExpressions;

namespace MCPHub.Proxy;

/// <summary>Options for <see cref="StaticToolAuthorization"/>: a fixed tenant → grant-patterns map.</summary>
public sealed class StaticToolAuthorizationOptions
{
    /// <summary>
    /// Grant patterns per tenant id. Each pattern is matched (ordinal, <c>*</c> = any run of
    /// characters) against three candidates for a tool: the upstream server key (grants the whole
    /// server, e.g. <c>azuredevops</c>), the namespaced tool name (<c>azuredevops__azdo_get_project</c>),
    /// and the upstream's original tool name (<c>azdo_get_project</c>, so <c>azdo_*</c> grants a
    /// family of tools regardless of the server key they are namespaced under).
    /// A tenant with no entry has no grants and sees an empty catalog.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Grants { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>
/// Pattern-grant authorization from a plain options object: visibility and callability are the same
/// decision (a tenant may call exactly what it can see). Intended for static per-tenant profiles
/// loaded from configuration; dynamic policies implement <see cref="IToolAuthorization"/> directly.
/// </summary>
public sealed class StaticToolAuthorization : IToolAuthorization
{
    private readonly Dictionary<string, Regex[]> _grants;

    public StaticToolAuthorization(StaticToolAuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _grants = options.Grants.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(CompilePattern).ToArray(),
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName)
        => IsGranted(tenant, serverKey, exposedToolName);

    /// <inheritdoc />
    public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName)
        => IsGranted(tenant, serverKey, exposedToolName);

    private bool IsGranted(TenantContext tenant, string serverKey, string exposedToolName)
    {
        if (!_grants.TryGetValue(tenant.TenantId, out var patterns))
            return false;

        var originalName = exposedToolName.StartsWith(serverKey + ProxyConstants.NamespaceSeparator, StringComparison.Ordinal)
            ? exposedToolName[(serverKey.Length + ProxyConstants.NamespaceSeparator.Length)..]
            : exposedToolName;

        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(serverKey) || pattern.IsMatch(exposedToolName) || pattern.IsMatch(originalName))
                return true;
        }

        return false;
    }

    private static Regex CompilePattern(string pattern)
        => new("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
