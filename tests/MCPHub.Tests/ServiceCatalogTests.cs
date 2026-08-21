using MCPHub.Core.Catalog;
using Xunit;

namespace MCPHub.Tests;

public class ServiceCatalogTests
{
    [Fact]
    public void Catalog_contains_all_seventeen_products()
    {
        Assert.Equal(17, ServiceCatalog.All.Count);
    }

    [Fact]
    public void Catalog_names_are_unique()
    {
        var distinct = ServiceCatalog.All.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ServiceCatalog.All.Count, distinct.Count());
    }

    [Theory]
    [InlineData("GithubMCPSharp", 5701)]
    [InlineData("RemoteAdminMCPSharp", 5706)]
    [InlineData("NoteworthyMCPSharp", 5711)]
    [InlineData("SQLMCPSharp", 5712)]
    [InlineData("RedisMCPSharp", 5713)]
    [InlineData("ComfyUIMCPSharp", 5715)]
    [InlineData("PortainerMCPSharp", 5716)]
    [InlineData("MailCalMCPSharp", 5717)]
    public void Known_default_ports_are_recorded(string name, int expectedPort)
    {
        var entry = ServiceCatalog.FindByName(name);
        Assert.NotNull(entry);
        Assert.Equal(expectedPort, entry!.DefaultPort);
    }

    [Fact]
    public void Default_ports_do_not_collide()
    {
        // DefaultPort is only a fallback for when a service's config cannot be read, but two
        // products sharing one fallback is exactly the situation that made MailCal and
        // Paperless-ngx unable to run side by side.
        var ports = ServiceCatalog.All
            .Where(e => e.DefaultPort is not null)
            .Select(e => e.DefaultPort!.Value)
            .ToList();

        Assert.Equal(ports.Count, ports.Distinct().Count());
    }

    [Fact]
    public void Portainer_is_catalogued_with_its_own_repo_and_prefix()
    {
        var entry = ServiceCatalog.FindByName("PortainerMCPSharp");

        Assert.NotNull(entry);
        Assert.Equal("PortainerMCPSharp", entry!.RepoName);
        Assert.Equal("PORTAINERMCP_", entry.EnvPrefix);
        Assert.Equal("portainer", entry.Key);
        Assert.Equal("PortainerMCPSharp.json", entry.ConfigFileName);
        Assert.Equal("PortainerMCPSharp-win-x64-self-contained-v0.1.0.zip",
            entry.AssetFileName("win", "self-contained", "v0.1.0"));
    }

    [Fact]
    public void Config_file_name_is_product_name_dot_json()
    {
        var entry = ServiceCatalog.FindByName("NoteworthyMCPSharp")!;
        Assert.Equal("NoteworthyMCPSharp.json", entry.ConfigFileName);
    }

    [Fact]
    public void Asset_file_name_follows_release_naming_convention()
    {
        var entry = ServiceCatalog.FindByName("PlaywrightMCPSharp")!;
        var asset = entry.AssetFileName("win", "self-contained", "v1.1.6");
        Assert.Equal("PlaywrightMCPSharp-win-x64-self-contained-v1.1.6.zip", asset);
    }

    [Theory]
    [InlineData("MailCalMCPSharp", "mailcal")]
    [InlineData("ComfyUIMCPSharp", "comfyui")]
    public void Proxy_key_is_the_product_name_minus_the_mcpsharp_suffix(string name, string expectedKey)
    {
        Assert.Equal(expectedKey, ServiceCatalog.FindByName(name)!.Key);
    }

    [Fact]
    public void RepoDetox_maps_the_mcp_product_onto_its_multi_app_repo()
    {
        var entry = ServiceCatalog.FindByName("RepoDetoxMCPSharp")!;

        // Non-standard: the MCP server ships inside the "RepoDetox" repo (with a CLI + GUI), so the
        // GitHub coordinates use the repo name while product/exe/config naming uses the product name.
        Assert.Equal("RepoDetox", entry.RepoName);
        Assert.Equal("https://api.github.com/repos/Wixely/RepoDetox/releases/latest", entry.LatestReleaseApiUrl);

        // The installer must resolve the MCP asset — not the RepoDetox-cli / RepoDetox-gui zips.
        Assert.Equal("RepoDetoxMCPSharp-win-x64-self-contained-v1.5.1.zip",
            entry.AssetFileName("win", "self-contained", "v1.5.1"));
        Assert.Equal("RepoDetoxMCPSharp.json", entry.ConfigFileName);
        Assert.Equal("repodetox", entry.Key);
    }
}
