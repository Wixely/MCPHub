namespace MCPHub.Core.Models;

/// <summary>Lifecycle state of a managed MCP sub-server process.</summary>
public enum ServiceRunState
{
    /// <summary>Not running.</summary>
    Stopped,

    /// <summary>Process started; waiting for <c>/healthz</c> to report ready.</summary>
    Starting,

    /// <summary>Process running and health checks passing.</summary>
    Running,

    /// <summary>Process alive but health checks failing.</summary>
    Unhealthy,

    /// <summary>Stop requested; process is shutting down.</summary>
    Stopping,

    /// <summary>Process exited unexpectedly (e.g. crashed during startup).</summary>
    Faulted,
}

/// <summary>Whether an installed service is up to date with its latest GitHub release.</summary>
public enum UpdateStatus
{
    /// <summary>Not yet checked against GitHub.</summary>
    Unknown,

    /// <summary>No copy installed in the shared servers folder.</summary>
    NotInstalled,

    /// <summary>Installed version matches the latest release.</summary>
    UpToDate,

    /// <summary>A newer release is available on GitHub.</summary>
    UpdateAvailable,
}

/// <summary>Release artifact flavour to download from GitHub.</summary>
public enum PublishFlavor
{
    /// <summary>Bundles the .NET runtime; no separate runtime install required (default).</summary>
    SelfContained,

    /// <summary>Smaller; requires a matching .NET runtime to be installed.</summary>
    FrameworkDependent,
}
