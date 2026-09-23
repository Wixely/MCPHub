namespace MCPHub.Core.Routing;

public sealed record RouterConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public int Port { get; init; } = 5801;

    /// <summary>
    /// Address the listener binds to. <c>127.0.0.1</c> (the default) keeps the Router on this machine;
    /// <c>0.0.0.0</c> accepts connections on every IPv4 interface, and <c>::</c> on every IPv6 one. Any
    /// literal address of a local interface also works, to bind one network only.
    /// </summary>
    public string BindAddress { get; init; } = RouterConfigurationRules.Loopback;

    public bool StartOnLaunch { get; init; }
    public string? DefaultOutputId { get; init; }
    public RouterInput[] Inputs { get; init; } = [];
    public RouterOutput[] Outputs { get; init; } = [];
}

public sealed record RouterInput
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string KeyHash { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string? OutputId { get; init; }
}

public sealed record RouterOutput
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string? Model { get; init; }
    public string? ProtectedApiKey { get; init; }
}

public sealed record RouterRoute(string InputId, RouterOutput? Output)
{
    // Captures the credential source with the route so configuration reload cannot mix generations.
    public Func<string?>? ReadApiKey { get; init; }
}
