namespace MCPHub.Core.Routing;

public sealed record RouterConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public int Port { get; init; } = RouterConfigurationRules.DefaultPort;

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

/// <summary>
/// A caller allowed through the Router, and where its requests go.
///
/// <para>Identity is not here: the caller is a <see cref="Users.HubUser"/>, and its name, its key and
/// whether it is suspended belong to the user directory. This record says only that the user may use
/// the Router and which output it gets — so one key works on the proxy and here, and suspending it
/// stops both.</para>
///
/// <para>A user with no input has no Router access at all. Absence refuses rather than falling back to
/// the default output, because a user created to reach a tool should not silently acquire a model route
/// as well.</para>
/// </summary>
public sealed record RouterInput
{
    /// <summary>The <see cref="Users.HubUser.Id"/> this input belongs to.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Output to route to, or null for <see cref="RouterConfiguration.DefaultOutputId"/>.</summary>
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

/// <summary>Where one authenticated caller's requests go, resolved at the moment of the call.</summary>
public sealed record RouterRoute(string UserId, RouterOutput? Output)
{
    // Captures the credential source with the route so configuration reload cannot mix generations.
    public Func<string?>? ReadApiKey { get; init; }
}
