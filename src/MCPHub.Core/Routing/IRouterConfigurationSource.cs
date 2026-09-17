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
    public string BindAddress { get; init; } = "127.0.0.1";
}
