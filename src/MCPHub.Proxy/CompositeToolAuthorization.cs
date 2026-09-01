namespace MCPHub.Proxy;

/// <summary>
/// Combines several <see cref="IToolAuthorization"/> policies: a tool is visible / callable only when
/// <em>every</em> policy agrees. Lets independent feature policies (each answering only for its own
/// server key and allowing everything else) be stacked in front of one <see cref="ProxyHandlers"/>.
/// With no policies it allows everything, like <see cref="AllowAllToolAuthorization"/>.
/// </summary>
public sealed class CompositeToolAuthorization : IToolAuthorization
{
    private readonly IReadOnlyList<IToolAuthorization> _policies;

    /// <summary>Creates a policy that consults <paramref name="policies"/> in order; none at all means allow everything.</summary>
    public CompositeToolAuthorization(params IToolAuthorization[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Any(p => p is null))
            throw new ArgumentException("Policies must not contain null.", nameof(policies));
        _policies = policies;
    }

    /// <summary>The policies consulted, in order.</summary>
    public IReadOnlyList<IToolAuthorization> Policies => _policies;

    /// <inheritdoc />
    public bool IsToolVisible(TenantContext tenant, string serverKey, string exposedToolName)
        => _policies.All(p => p.IsToolVisible(tenant, serverKey, exposedToolName));

    /// <inheritdoc />
    public bool IsCallAllowed(TenantContext tenant, string serverKey, string exposedToolName)
        => _policies.All(p => p.IsCallAllowed(tenant, serverKey, exposedToolName));
}
