using MCPHub.Proxy;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// What tools exist, grouped by what offers them — the list the Permissions page turns into
/// checkboxes, so that granting a tool means ticking it rather than typing its namespaced name
/// correctly from memory.
/// </summary>
public sealed class ToolCatalogTests
{
    private sealed class StubProvider(string key, string name, params string[] tools) : ILocalToolProvider
    {
        public string Key => key;

        public string DisplayName => name;

        public IReadOnlyList<Tool> Tools => [.. tools.Select(t => new Tool { Name = t })];

        public ValueTask<CallToolResult> CallAsync(
            string toolName,
            IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static UpstreamServer Server(string key, string name) =>
        new() { Key = key, DisplayName = name, Endpoint = "http://localhost/mcp" };

    [Fact]
    public void A_connected_service_is_listed_with_its_tools_under_its_own_name()
    {
        var registry = ProxyTestKit.RegistryWith(("kodi", "play_pause"), ("kodi", "stop"));
        registry.Upstreams = [Server("kodi", "Kodi")];

        var group = Assert.Single(new ToolCatalog(registry, []).Groups);

        Assert.Equal("kodi", group.Key);
        Assert.Equal("Kodi", group.DisplayName);
        Assert.Equal(ToolGroupKind.Service, group.Kind);
        Assert.True(group.IsAvailable);
        Assert.Equal(["kodi__play_pause", "kodi__stop"], group.Tools);
    }

    /// <summary>
    /// A registered service that is not connected offers nothing, and saying so is the point: its
    /// tools cannot be ticked one by one, so the whole-service checkbox is the only way to grant it.
    /// </summary>
    [Fact]
    public void A_registered_service_with_no_connection_is_listed_as_unavailable()
    {
        var registry = ProxyTestKit.RegistryWith();
        registry.Upstreams = [Server("redis", "Redis")];

        var group = Assert.Single(new ToolCatalog(registry, []).Groups);

        Assert.False(group.IsAvailable);
        Assert.Empty(group.Tools);
    }

    /// <summary>The hub's own tools are groups too, so "recipes" and "users" read like any service
    /// rather than as loose names somebody has to recognise.</summary>
    [Fact]
    public void The_hubs_own_providers_are_groups_of_their_own()
    {
        var catalog = new ToolCatalog(
            ProxyTestKit.RegistryWith(),
            [new StubProvider("recipes", "Recipes", "list", "add")]);

        var group = Assert.Single(catalog.Groups);

        Assert.Equal(ToolGroupKind.Hub, group.Kind);
        Assert.Equal("Recipes", group.DisplayName);
        Assert.Equal(["recipes__add", "recipes__list"], group.Tools);
        Assert.True(group.IsAvailable);
    }

    [Fact]
    public void Services_come_before_the_hubs_own_tools()
    {
        var registry = ProxyTestKit.RegistryWith(("kodi", "stop"));
        registry.Upstreams = [Server("kodi", "Kodi")];

        var groups = new ToolCatalog(registry, [new StubProvider("users", "Users", "list")]).Groups;

        Assert.Equal(["kodi", "users"], groups.Select(g => g.Key));
    }

    /// <summary>
    /// Tools whose service has gone from the registry but whose routes are still live can still be
    /// called, so they are still grantable — leaving them out would hide tools a user can use.
    /// </summary>
    [Fact]
    public void Tools_with_no_registered_service_are_still_listed_under_their_key()
    {
        var registry = ProxyTestKit.RegistryWith(("orphan", "do_thing"));

        var group = Assert.Single(new ToolCatalog(registry, []).Groups);

        Assert.Equal("orphan", group.Key);
        Assert.Equal(["orphan__do_thing"], group.Tools);
    }

    [Fact]
    public void An_empty_hub_offers_nothing_rather_than_failing()
    {
        Assert.Empty(new ToolCatalog(ProxyTestKit.RegistryWith(), []).Groups);
    }
}
