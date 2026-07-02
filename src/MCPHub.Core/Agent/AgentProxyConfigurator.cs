using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPHub.Core.Agent;

/// <summary>
/// Points DaggerAgent's <c>appsettings.json</c> at the MCPHub proxy by ensuring an <c>Mcp.Servers[]</c>
/// entry named <see cref="ProxyServerName"/> with the proxy URL. On a first install the shipped example
/// servers are replaced with just the proxy; on later installs the proxy entry is added or its URL
/// refreshed while the user's other servers are left intact.
/// </summary>
public static class AgentProxyConfigurator
{
    /// <summary>Name of the <c>Mcp.Servers</c> entry MCPHub manages.</summary>
    public const string ProxyServerName = "mcphub";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Builds the proxy MCP endpoint URL, e.g. <c>http://127.0.0.1:5800/mcp</c>.</summary>
    public static string ProxyUrl(string bindAddress, int port) => $"http://{bindAddress}:{port}/mcp";

    /// <summary>
    /// Returns <paramref name="appsettingsJson"/> with the MCPHub proxy wired into <c>Mcp.Servers</c>.
    /// When <paramref name="replaceExisting"/> is <see langword="true"/> the servers list is replaced with
    /// just the proxy (first install); otherwise the proxy entry is added, or its URL refreshed, while
    /// keeping any other servers the user configured.
    /// </summary>
    public static string WireProxy(string appsettingsJson, string proxyUrl, bool replaceExisting)
    {
        var root = JsonNode.Parse(appsettingsJson) as JsonObject ?? new JsonObject();

        if (root["Mcp"] is not JsonObject mcp)
        {
            mcp = new JsonObject();
            root["Mcp"] = mcp;
        }

        if (replaceExisting || mcp["Servers"] is not JsonArray servers)
        {
            mcp["Servers"] = new JsonArray(NewProxy(proxyUrl));
            return root.ToJsonString(Indented);
        }

        foreach (var node in servers)
        {
            if (node is JsonObject o &&
                string.Equals((string?)o["Name"], ProxyServerName, StringComparison.OrdinalIgnoreCase))
            {
                o["Url"] = proxyUrl;
                return root.ToJsonString(Indented);
            }
        }

        servers.Add(NewProxy(proxyUrl));
        return root.ToJsonString(Indented);
    }

    private static JsonObject NewProxy(string proxyUrl)
        => new() { ["Name"] = ProxyServerName, ["Url"] = proxyUrl };
}
