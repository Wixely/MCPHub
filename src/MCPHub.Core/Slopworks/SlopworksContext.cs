using MCPHub.Core.Models;

namespace MCPHub.Core.Slopworks;

/// <summary>
/// Singleton holder for the one <see cref="ManagedService"/> instance that represents Slopworks, so
/// the install/check service, the CLI wrapper, and the view-model share the same install/version
/// state without a dependency cycle.
/// </summary>
public sealed class SlopworksContext
{
    public SlopworksContext(ManagedService slopworks) => Slopworks = slopworks;

    /// <summary>The Slopworks managed-service state (installed in its own dedicated folder).</summary>
    public ManagedService Slopworks { get; }
}
