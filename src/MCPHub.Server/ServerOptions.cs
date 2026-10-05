using System.Globalization;

namespace MCPHub.Server;

/// <summary>
/// The listener, and whether this hub may be administered through it.
///
/// <para>Read from the environment because that is what a container has. Each one is also a desktop
/// setting; the difference is that a container's answer must be decided before anything starts and
/// cannot be clicked afterwards.</para>
/// </summary>
/// <param name="BindAddress">What Kestrel binds. <c>0.0.0.0</c> by default, since a container that
/// bound loopback would be reachable only from inside itself.</param>
/// <param name="Port">The port inside the container.</param>
/// <param name="Administration">Whether <c>users__*</c> and <c>permissions__*</c> are offered, or
/// null to leave the stored setting alone. Off unless asked for: those tools govern every other
/// tool.</param>
public sealed record ServerOptions(string BindAddress, int Port, bool? Administration)
{
    public const string BindVariable = "MCPHUB_PROXY_BIND";
    public const string PortVariable = "MCPHUB_PROXY_PORT";

    /// <summary>The same variable the desktop's switch reads, so one deployment cannot mean two
    /// things depending on which head is running.</summary>
    public const string AdministrationVariable = Core.Users.AdministrationPolicy.EnabledVariable;

    public static ServerOptions FromEnvironment(Func<string, string?>? environment = null)
    {
        var read = environment ?? Environment.GetEnvironmentVariable;

        var port = 5800;
        if (read(PortVariable) is { Length: > 0 } portText)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
            {
                throw new ArgumentException($"{PortVariable} must be a port number between 1 and 65535.");
            }
        }

        return new ServerOptions(
            read(BindVariable) is { Length: > 0 } bind ? bind : "0.0.0.0",
            port,
            Flag(read(AdministrationVariable)));
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
