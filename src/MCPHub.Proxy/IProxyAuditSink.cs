namespace MCPHub.Proxy;

/// <summary>How a proxied tool call ended.</summary>
public enum ToolCallOutcome
{
    /// <summary>The upstream call completed and did not report a tool error.</summary>
    Success,

    /// <summary>Authorization refused the call (or the tool was invisible to the tenant).</summary>
    Denied,

    /// <summary>The upstream call failed, or the tool itself reported an error result.</summary>
    Error,
}

/// <summary>
/// One audited tool call. Carries a SHA-256 digest of the arguments JSON, never the arguments
/// themselves: the audit trail answers "what did that tenant touch?" — deliberately not
/// "what did it say?".
/// </summary>
/// <param name="TenantId">Tenant the call was made as.</param>
/// <param name="Tool">Namespaced tool name, e.g. <c>noteworthy__list_notes</c>.</param>
/// <param name="ArgumentsSha256">Lowercase hex SHA-256 of the arguments JSON (<c>{}</c> when absent).</param>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Duration">Wall time from receipt to outcome.</param>
/// <param name="TimestampUtc">UTC time the outcome was recorded.</param>
public sealed record ToolCallAuditEvent(
    string TenantId,
    string Tool,
    string ArgumentsSha256,
    ToolCallOutcome Outcome,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Receives one event per proxied tool call. Implementations must be thread-safe and fast
/// (called inline on the request path); queue internally if delivery is slow.
/// </summary>
public interface IProxyAuditSink
{
    void Record(ToolCallAuditEvent auditEvent);
}

/// <summary>Discards all audit events. The default.</summary>
public sealed class NullProxyAuditSink : IProxyAuditSink
{
    /// <summary>Shared instance.</summary>
    public static readonly NullProxyAuditSink Instance = new();

    /// <inheritdoc />
    public void Record(ToolCallAuditEvent auditEvent)
    {
    }
}
