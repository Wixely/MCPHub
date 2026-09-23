namespace MCPHub.Core.Routing;

/// <summary>Runtime boundary shared by desktop storage and read-only deployment configuration.</summary>
public interface IRouterConfigurationSource
{
    RouterConfiguration Snapshot { get; }
    string? LoadError { get; }
    RouterRoute? Resolve(string key);
}

/// <summary>Listener settings owned by the host, not the desktop editor.</summary>
public sealed record RouterHostOptions
{
    /// <summary>
    /// Pins the bind address for hosts whose deployment decides it (a container's <c>MCPHUB_ROUTER_BIND</c>,
    /// say), overriding the configuration. <see langword="null"/> — the default — takes
    /// <see cref="RouterConfiguration.BindAddress"/> instead, so the desktop can change it while running.
    /// </summary>
    public string? BindAddress { get; init; }
}
