using MCPHub.Core.Models;

namespace MCPHub.Core.Agent;

/// <summary>
/// Singleton holder for the one <see cref="ManagedService"/> instance that represents DaggerAgent, so the
/// install/check service and the process host share the same install/version/run state without a
/// dependency cycle.
/// </summary>
public sealed class AgentContext
{
    public AgentContext(ManagedService agent) => Agent = agent;

    /// <summary>The DaggerAgent managed-service state (installed in its own dedicated folder).</summary>
    public ManagedService Agent { get; }
}
