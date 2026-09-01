using MCPHub.Core.Recipes;
using MCPHub.Core.Settings;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;
using static MCPHub.Tests.ProxyTestKit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

public class RecipeAccessPolicyTests
{
    private static readonly string[] AllRecipeTools = ["recipes__list", "recipes__get", "recipes__add", "recipes__update", "recipes__remove"];
    private static readonly string[] ReadOnlyRecipeTools = ["recipes__list", "recipes__get"];

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new();
        public Dictionary<string, string?> Env { get; } = new(StringComparer.Ordinal);
        public SettingsStore Settings { get; }
        public RecipeStore Store { get; }
        public RecipeAccessPolicy Policy { get; }

        public Fixture()
        {
            var paths = new FakeAppPaths(_dir.Path);
            Settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            Store = new RecipeStore(paths, NullLogger<RecipeStore>.Instance);
            Policy = new RecipeAccessPolicy(Settings, name => Env.GetValueOrDefault(name));
        }

        /// <summary>Handlers as Composition.cs builds them: one upstream, the recipes provider, this policy.</summary>
        public ProxyHandlers Handlers(CollectingAuditSink? sink = null) => new(
            RegistryWith(("kodi", "kodi_list_instances")),
            Policy, sink, null, [new RecipeToolProvider(Store)]);

        public void Dispose() => _dir.Dispose();
    }

    private static List<string> Names(ProxyHandlers handlers)
        => handlers.ListTools(TenantContext.Default).Tools.Select(t => t.Name).ToList();

    [Fact]
    public void Defaults_allow_agents_to_read_and_write()
    {
        using var f = new Fixture();

        Assert.True(f.Policy.RecipesEnabled);
        Assert.True(f.Policy.AgentEditEnabled);
        Assert.Null(f.Policy.RecipesEnabledOverrideSource);
        Assert.Null(f.Policy.AgentEditOverrideSource);
        Assert.Equal(RecipeToolProvider.ServerInstructions, f.Policy.ServerInstructions);
        Assert.Equal(["kodi__kodi_list_instances", .. AllRecipeTools], Names(f.Handlers()));
    }

    [Fact]
    public async Task Disabling_recipes_hides_every_recipe_tool_and_refuses_calls_but_leaves_other_tools_alone()
    {
        using var f = new Fixture();
        var sink = new CollectingAuditSink();
        var handlers = f.Handlers(sink);
        f.Settings.Current.RecipesEnabled = false;

        Assert.Equal(["kodi__kodi_list_instances"], Names(handlers));
        Assert.Null(f.Policy.ServerInstructions);

        var result = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "recipes__list" }, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", Text(result));
        Assert.Equal(ToolCallOutcome.Denied, Assert.Single(sink.Events).Outcome);
    }

    [Fact]
    public async Task Disabling_agent_edit_leaves_list_and_get_only()
    {
        using var f = new Fixture();
        var handlers = f.Handlers();
        f.Settings.Current.RecipesAgentEditEnabled = false;

        Assert.False(f.Policy.AgentEditEnabled);
        Assert.Equal(["kodi__kodi_list_instances", .. ReadOnlyRecipeTools], Names(handlers));
        Assert.Contains("read-only", f.Policy.ServerInstructions);

        var add = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "recipes__add", Arguments = Args(("title", "t"), ("when", "w"), ("then", "x")) },
            CancellationToken.None);
        Assert.True(add.IsError);
        Assert.Empty(f.Store.All);

        var list = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "recipes__list" }, CancellationToken.None);
        Assert.NotEqual(true, list.IsError);
    }

    [Fact]
    public void Changes_take_effect_immediately_without_rebuilding_handlers()
    {
        using var f = new Fixture();
        var handlers = f.Handlers();

        Assert.Equal(5, Names(handlers).Count(n => n.StartsWith("recipes__", StringComparison.Ordinal)));
        f.Settings.Current.RecipesAgentEditEnabled = false;
        Assert.Equal(2, Names(handlers).Count(n => n.StartsWith("recipes__", StringComparison.Ordinal)));
        f.Settings.Current.RecipesEnabled = false;
        Assert.Equal(0, Names(handlers).Count(n => n.StartsWith("recipes__", StringComparison.Ordinal)));
        f.Settings.Current.RecipesEnabled = true;
        f.Settings.Current.RecipesAgentEditEnabled = true;
        Assert.Equal(5, Names(handlers).Count(n => n.StartsWith("recipes__", StringComparison.Ordinal)));
    }

    [Fact]
    public void Environment_overrides_win_over_settings_and_are_reported()
    {
        using var f = new Fixture();
        f.Settings.Current.RecipesEnabled = true;
        f.Settings.Current.RecipesAgentEditEnabled = true;

        f.Env[RecipeAccessPolicy.AgentEditVariable] = "false";
        Assert.True(f.Policy.RecipesEnabled);
        Assert.False(f.Policy.AgentEditEnabled);
        Assert.Null(f.Policy.RecipesEnabledOverrideSource);
        Assert.Equal("MCPHUB_RECIPES_AGENT_EDIT=false", f.Policy.AgentEditOverrideSource);

        f.Env[RecipeAccessPolicy.EnabledVariable] = " 0 ";
        Assert.False(f.Policy.RecipesEnabled);
        Assert.Equal("MCPHUB_RECIPES_ENABLED=0", f.Policy.RecipesEnabledOverrideSource);
        Assert.Equal(["kodi__kodi_list_instances"], Names(f.Handlers()));

        // The override can also force the feature ON when the persisted setting has it off.
        f.Settings.Current.RecipesEnabled = false;
        f.Env[RecipeAccessPolicy.EnabledVariable] = "yes";
        f.Env.Remove(RecipeAccessPolicy.AgentEditVariable);
        Assert.True(f.Policy.RecipesEnabled);
        Assert.True(f.Policy.AgentEditEnabled);
    }

    [Fact]
    public void Edit_flag_is_moot_while_recipes_are_off()
    {
        using var f = new Fixture();
        f.Settings.Current.RecipesEnabled = false;
        f.Settings.Current.RecipesAgentEditEnabled = true;

        Assert.False(f.Policy.AgentEditEnabled);
        Assert.True(f.Policy.AgentEditSwitch, "the user's choice is kept for when recipes come back on");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("On", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("NO", false)]
    [InlineData("off", false)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("maybe", null)]
    public void Flags_parse_the_way_containers_spell_them(string? raw, bool? expected)
    {
        Assert.Equal(expected, RecipeAccessPolicy.ParseFlag(raw));
    }

    [Fact]
    public async Task Settings_roundtrip_the_two_switches()
    {
        using var dir = new TempDir();
        var paths = new FakeAppPaths(dir.Path);

        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        Assert.True(store.Current.RecipesEnabled);
        Assert.True(store.Current.RecipesAgentEditEnabled);
        store.Current.RecipesEnabled = false;
        store.Current.RecipesAgentEditEnabled = false;
        await store.SaveAsync();

        var reloaded = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        Assert.False(reloaded.Current.RecipesEnabled);
        Assert.False(reloaded.Current.RecipesAgentEditEnabled);
    }
}
