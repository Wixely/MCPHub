using MCPHub.Core.Agent;
using MCPHub.Core.Catalog;
using Xunit;

namespace MCPHub.Tests;

public class AgentTests
{
    [Fact]
    public void DaggerAgent_decouples_exe_and_config_from_the_product_name()
    {
        var e = DaggerAgent.Catalog;

        Assert.Equal("dagger.exe", e.ExecutableFileName(isWindows: true));
        Assert.Equal("dagger", e.ExecutableFileName(isWindows: false));
        Assert.Equal("appsettings.json", e.ConfigFileName);
        Assert.Equal("DaggerAgent-win-x64-self-contained-v1.1.1.zip", e.AssetFileName("win", "self-contained", "v1.1.1"));
        Assert.Equal("https://api.github.com/repos/Wixely/DaggerAgent/releases/latest", e.LatestReleaseApiUrl);
    }

    [Fact]
    public void Mcp_entries_keep_their_default_exe_and_config_names()
    {
        var noteworthy = ServiceCatalog.FindByName("NoteworthyMCPSharp")!;

        Assert.Equal("NoteworthyMCPSharp.exe", noteworthy.ExecutableFileName(isWindows: true));
        Assert.Equal("NoteworthyMCPSharp.json", noteworthy.ConfigFileName);
    }

    [Fact]
    public void WireProxy_first_install_replaces_servers_with_just_the_proxy()
    {
        const string shipped = """
        { "Mcp": { "Servers": [ { "Name": "github-http", "Url": "http://localhost:5101/mcp" } ] },
          "OpenAI": { "Model": "gpt" } }
        """;

        var wired = AgentProxyConfigurator.WireProxy(shipped, "http://127.0.0.1:5800/mcp", replaceExisting: true);

        Assert.Contains("http://127.0.0.1:5800/mcp", wired);
        Assert.DoesNotContain("5101", wired);          // example server dropped
        Assert.Contains("mcphub", wired);
        Assert.Contains("gpt", wired);                 // unrelated config preserved
    }

    [Fact]
    public void WireProxy_update_adds_the_proxy_and_keeps_other_servers()
    {
        const string existing = """{ "Mcp": { "Servers": [ { "Name": "fs", "Command": "npx" } ] } }""";

        var wired = AgentProxyConfigurator.WireProxy(existing, "http://127.0.0.1:5800/mcp", replaceExisting: false);

        Assert.Contains("fs", wired);       // kept
        Assert.Contains("mcphub", wired);   // added
        Assert.Contains("5800", wired);
    }

    [Fact]
    public void WireProxy_refreshes_the_url_of_an_existing_proxy_entry()
    {
        const string existing = """{ "Mcp": { "Servers": [ { "Name": "mcphub", "Url": "http://127.0.0.1:9999/mcp" } ] } }""";

        var wired = AgentProxyConfigurator.WireProxy(existing, "http://127.0.0.1:5800/mcp", replaceExisting: false);

        Assert.Contains("5800", wired);
        Assert.DoesNotContain("9999", wired);
    }
}
