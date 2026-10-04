namespace MCPHub.Proxy;

/// <summary>
/// Combines several <see cref="IToolAuthorization"/> policies: a tool is visible / callable only when
/// <em>every</em> policy agrees. Lets independent feature policies (each answering only for its own
/// server key and allowing everything else) be stacked in front of one <see cref="ProxyHandlers"/>.
/// With no policies it allows everything, like <see cref="AllowAllToolAuthorization"/>.
/// </summary>
public sealed class CompositeToolAuthorization : IToolAuthorization, IToolAuthorizationDiagnostics
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

    /// <summary>
    /// The first denial, for callers wanting one reason. Prefer <see cref="ExplainAll"/>: because this
    /// is an AND, several policies can be denying at once and fixing one changes nothing.
    /// </summary>
    public ToolDenial? Explain(TenantContext tenant, string serverKey, string exposedToolName)
        => ExplainAll(tenant, serverKey, exposedToolName).FirstOrDefault();

    /// <summary>
    /// <b>Every</b> reason this tool is unavailable, or empty when it is available.
    ///
    /// <para>All of them, deliberately. Reporting only the first is how an operator ends up granting a
    /// tool, seeing no change, and concluding the grant did not work — when a second policy was also
    /// denying it all along.</para>
    ///
    /// <para>A policy that denies without implementing <see cref="IToolAuthorizationDiagnostics"/>
    /// still gets a denial, naming its type. "Denied, and nothing can say why" is a worse answer than
    /// a vague one, and naming the type at least says where to look.</para>
    /// </summary>
    public IReadOnlyList<ToolDenial> ExplainAll(TenantContext tenant, string serverKey, string exposedToolName)
    {
        var denials = new List<ToolDenial>();
        foreach (var policy in _policies)
        {
            if (policy.IsToolVisible(tenant, serverKey, exposedToolName)
                && policy.IsCallAllowed(tenant, serverKey, exposedToolName))
            {
                continue;
            }

            denials.Add(
                (policy as IToolAuthorizationDiagnostics)?.Explain(tenant, serverKey, exposedToolName)
                ?? new ToolDenial
                {
                    Code = UndiagnosedCode,
                    Reason = $"{policy.GetType().Name} denies this tool and cannot explain why.",
                    Remedy = "Inspect that policy's configuration directly.",
                });
        }

        return denials;
    }

    /// <summary>Raised for a policy that denies a tool but implements no diagnostics.</summary>
    public const string UndiagnosedCode = "policy.undiagnosed";
}
