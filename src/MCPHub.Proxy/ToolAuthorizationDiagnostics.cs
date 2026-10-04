namespace MCPHub.Proxy;

/// <summary>
/// One reason a tool is unavailable, in a form an operator can act on.
///
/// <para><b>Operator-facing, never caller-facing.</b> The proxy's whole authorization stance is that
/// an ungranted tool is <em>absent</em> rather than refused, so a tenant cannot discover what it may
/// not use. Handing a denial reason back to the denied caller would undo that. These are produced for
/// whoever administers the hub, through the management surface, about a user that is not the
/// caller asking.</para>
///
/// <para>The reason this type exists is that <see cref="CompositeToolAuthorization"/> is an AND over
/// independent policies, so "my tool is missing" has several possible causes at once — a grant that
/// was never given <em>and</em> a feature switch pinned off by an environment variable, say. One
/// boolean cannot say which, and an operator with two configuration files to check and no indication
/// of which is wrong will change the wrong one.</para>
/// </summary>
public sealed record ToolDenial
{
    /// <summary>
    /// Stable, dotted, machine-readable: <c>permissions.no_grant</c>,
    /// <c>agent_management.capability_off</c>. Consumers branch on this; the prose may be reworded.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>What is wrong, as a sentence naming the specific thing.</summary>
    public required string Reason { get; init; }

    /// <summary>What to change to fix it, when there is a single clear answer.</summary>
    public string? Remedy { get; init; }

    /// <summary>
    /// The environment variable forcing this, when one is. Set this and the operator stops editing a
    /// settings file that cannot win — the single most confusing failure the hub has, because the UI
    /// shows a checkbox that will not stay where it is put.
    /// </summary>
    public string? PinnedBy { get; init; }
}

/// <summary>
/// Optional companion to <see cref="IToolAuthorization"/>: explains a denial rather than only
/// reporting one.
///
/// <para>Separate from <see cref="IToolAuthorization"/> on purpose. That interface is published and
/// implemented outside this repository, so widening it would break consumers; and a policy that can
/// only answer yes or no is still a perfectly good policy. Policies that can explain opt in.</para>
/// </summary>
public interface IToolAuthorizationDiagnostics
{
    /// <summary>
    /// Why <paramref name="exposedToolName"/> is unavailable to <paramref name="tenant"/>, or
    /// <see langword="null"/> when this policy allows it.
    ///
    /// <para>Implementations must agree with their own <see cref="IToolAuthorization"/> answer: a
    /// policy that denies but explains nothing, or explains but allows, is worse than one that does
    /// not implement this at all. Deriving the allow case from the same predicate the policy enforces
    /// with is the way to be sure.</para>
    /// </summary>
    ToolDenial? Explain(TenantContext tenant, string serverKey, string exposedToolName);
}
