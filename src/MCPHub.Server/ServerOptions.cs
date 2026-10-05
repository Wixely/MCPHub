using System.Globalization;
using MCPHub.Core.Routing;

namespace MCPHub.Server;

/// <summary>
/// What this container serves, where it listens, and whether it may be administered through it.
///
/// <para>Read from the environment because that is what a container has. Each one is also a desktop
/// setting; the difference is that a container's answer must be decided before anything starts and
/// cannot be clicked afterwards.</para>
///
/// <para><b>Two listeners, independently switched.</b> The proxy carries tools and the Router carries
/// model requests — different protocols on different ports, wanted separately as often as together.
/// They are one image because they are one hub: the same users document answers for both, so a key
/// issued here reaches a tool and a model, and suspending it stops both. Running them as two
/// containers would mean two copies of that document and no way to keep them agreeing.</para>
/// </summary>
/// <param name="Proxy">Whether to serve the MCP proxy. On unless switched off.</param>
/// <param name="BindAddress">What the proxy binds. <c>0.0.0.0</c> by default, since a container that
/// bound loopback would be reachable only from inside itself.</param>
/// <param name="Port">The proxy's port inside the container.</param>
/// <param name="Router">Whether to serve the Model Router. Off unless asked for: it forwards to
/// upstream providers, and a listener nobody has configured an output for answers every request with
/// a 503.</param>
/// <param name="RouterBindAddress">What the Router binds.</param>
/// <param name="RouterPort">The Router's port inside the container.</param>
/// <param name="Administration">Whether <c>users__*</c> and <c>permissions__*</c> are offered, or
/// null to leave the stored setting alone. Off unless asked for: those tools govern every other
/// tool.</param>
public sealed record ServerOptions(
    bool Proxy,
    string BindAddress,
    int Port,
    bool Router,
    string RouterBindAddress,
    int RouterPort,
    bool? Administration)
{
    public const string ProxyEnabledVariable = "MCPHUB_PROXY_ENABLED";
    public const string BindVariable = "MCPHUB_PROXY_BIND";
    public const string PortVariable = "MCPHUB_PROXY_PORT";

    public const string RouterEnabledVariable = "MCPHUB_ROUTER_ENABLED";

    /// <summary>The names the headless Router already used, so a compose file moving onto this image
    /// keeps the lines it had.</summary>
    public const string RouterBindVariable = "MCPHUB_ROUTER_BIND";

    /// <inheritdoc cref="RouterBindVariable"/>
    public const string RouterPortVariable = "MCPHUB_ROUTER_PORT";

    /// <summary>The same variable the desktop's switch reads, so one deployment cannot mean two
    /// things depending on which head is running.</summary>
    public const string AdministrationVariable = Core.Users.AdministrationPolicy.EnabledVariable;

    public static ServerOptions FromEnvironment(Func<string, string?>? environment = null)
    {
        var read = environment ?? Environment.GetEnvironmentVariable;

        var options = new ServerOptions(
            Proxy: Flag(read(ProxyEnabledVariable)) ?? true,
            BindAddress: read(BindVariable) is { Length: > 0 } bind ? bind : "0.0.0.0",
            Port: ParsePort(read(PortVariable), 5800, PortVariable),
            Router: Flag(read(RouterEnabledVariable)) ?? false,
            RouterBindAddress: read(RouterBindVariable) is { Length: > 0 } routerBind ? routerBind : "0.0.0.0",
            RouterPort: ParsePort(read(RouterPortVariable), 5801, RouterPortVariable),
            Administration: Flag(read(AdministrationVariable)));

        options.Validate();
        return options;
    }

    /// <summary>
    /// Refuses a configuration that could only disappoint, before anything binds.
    ///
    /// <para>Both switched off is the one worth catching: a container that starts, logs nothing
    /// wrong and answers nothing is indistinguishable from a broken one, and somebody would look
    /// for the fault in their network.</para>
    /// </summary>
    public void Validate()
    {
        if (!Proxy && !Router)
        {
            throw new ArgumentException(
                $"Nothing to serve: {ProxyEnabledVariable} and {RouterEnabledVariable} are both off.");
        }

        // Kestrel's own failure for this is a bind error naming neither listener, on whichever
        // happens to come up second.
        if (Proxy && Router && Port == RouterPort
            && string.Equals(BindAddress, RouterBindAddress, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The proxy and the Router cannot share {BindAddress}:{Port}. "
                + $"Give one of them a port of its own with {PortVariable} or {RouterPortVariable}.");
        }

        if (Router)
        {
            // Parsed now rather than at first request: a bad address should stop a container starting.
            RouterConfigurationRules.ParseBindAddress(RouterBindAddress);
        }
    }

    private static int ParsePort(string? value, int fallback, string variable)
    {
        if (value is not { Length: > 0 })
        {
            return fallback;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException($"{variable} must be a port number between 1 and 65535.");
        }

        return port;
    }

    /// <summary>The same spellings the desktop's environment overrides accept, so a compose file
    /// written for one reads the same to the other.</summary>
    private static bool? Flag(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => null,
    };
}
