using MCPHub.Core.Management;
using MCPHub.Core.Recipes;
using MCPHub.Core.Settings;
using MCPHub.Hosting;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;
using static MCPHub.Tests.ManagementTestKit;
using static MCPHub.Tests.ProxyTestKit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

public class AgentManagementPolicyTests
{
    private static readonly string[] AllTools =
    [
        "mcphub__list_services", "mcphub__start", "mcphub__stop", "mcphub__restart",
        "mcphub__install", "mcphub__update", "mcphub__check_service_updates", "mcphub__check_hub_update",
    ];

    private static readonly string[] ControlTools = ["mcphub__start", "mcphub__stop", "mcphub__restart"];
    private static readonly string[] InstallTools = ["mcphub__install", "mcphub__update"];
    private static readonly string[] UpdateCheckTools = ["mcphub__check_service_updates", "mcphub__check_hub_update"];

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _dir = new();

        public Dictionary<string, string?> Env { get; } = new(StringComparer.Ordinal);
        public SettingsStore Settings { get; }
        public AgentManagementPolicy Policy { get; }
        public FakeProcessHost Host { get; } = new();
        public FakeServiceManager Manager { get; }
        public AgentManagementToolProvider Provider { get; }

        public Fixture()
        {
            var paths = new FakeAppPaths(_dir.Path);
            Settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            Policy = new AgentManagementPolicy(Settings, name => Env.GetValueOrDefault(name));
            Manager = new FakeServiceManager(Path.Combine(_dir.Path, "servers"), Host, "KodiMCPSharp");
            Provider = ManagementTestKit.Provider(Manager, Host);
        }

        /// <summary>Handlers as Composition.cs builds them, minus recipes: one upstream, the management provider, this policy.</summary>
        public ProxyHandlers Handlers(CollectingAuditSink? sink = null) => new(
            RegistryWith(("kodi", "kodi_list_instances")),
            Policy, sink, null, [Provider]);

        public void Dispose() => _dir.Dispose();
    }

    private static List<string> Names(ProxyHandlers handlers)
        => handlers.ListTools(TenantContext.Default).Tools.Select(t => t.Name).ToList();

    private static List<string> ManagementNames(ProxyHandlers handlers)
        => Names(handlers).Where(n => n.StartsWith("mcphub__", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Off_by_default_hides_every_management_tool_and_refuses_calls_but_leaves_other_tools_alone()
    {
        using var f = new Fixture();
        var sink = new CollectingAuditSink();
        var handlers = f.Handlers(sink);

        Assert.False(f.Policy.ManagementEnabled);
        Assert.False(f.Policy.ControlEnabled);
        Assert.False(f.Policy.InstallEnabled);
        Assert.False(f.Policy.UpdateChecksEnabled);
        Assert.Null(f.Policy.ServerInstructions);
        Assert.Equal(["kodi__kodi_list_instances"], Names(handlers));

        var result = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "mcphub__list_services" }, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", Text(result));
        Assert.Equal(ToolCallOutcome.Denied, Assert.Single(sink.Events).Outcome);
    }

    [Fact]
    public void Enabling_the_master_switch_exposes_everything_because_the_capability_switches_default_on()
    {
        using var f = new Fixture();
        var handlers = f.Handlers();
        f.Settings.Current.AgentManagementEnabled = true;

        Assert.True(f.Policy.ControlEnabled);
        Assert.True(f.Policy.InstallEnabled);
        Assert.True(f.Policy.UpdateChecksEnabled);
        Assert.Equal(["kodi__kodi_list_instances", .. AllTools], Names(handlers));

        var instructions = f.Policy.ServerInstructions!;
        Assert.Contains("mcphub__list_services", instructions);
        Assert.Contains("mcphub__start", instructions);
        Assert.Contains("mcphub__install", instructions);
        Assert.Contains("mcphub__check_hub_update", instructions);
        Assert.Contains("logs are not available", instructions);
    }

    [Fact]
    public async Task Each_capability_switch_hides_only_its_own_tools()
    {
        using var f = new Fixture();
        var sink = new CollectingAuditSink();
        var handlers = f.Handlers(sink);
        f.Settings.Current.AgentManagementEnabled = true;

        f.Settings.Current.AgentManagementControlEnabled = false;
        Assert.Equal(AllTools.Except(ControlTools), ManagementNames(handlers));
        Assert.DoesNotContain("mcphub__start", f.Policy.ServerInstructions);
        Assert.Contains("mcphub__install", f.Policy.ServerInstructions);
        f.Settings.Current.AgentManagementControlEnabled = true;

        f.Settings.Current.AgentManagementInstallEnabled = false;
        Assert.Equal(AllTools.Except(InstallTools), ManagementNames(handlers));
        Assert.DoesNotContain("mcphub__install", f.Policy.ServerInstructions);
        f.Settings.Current.AgentManagementInstallEnabled = true;

        f.Settings.Current.AgentManagementUpdateChecksEnabled = false;
        Assert.Equal(AllTools.Except(UpdateCheckTools), ManagementNames(handlers));
        Assert.DoesNotContain("mcphub__check_hub_update", f.Policy.ServerInstructions);

        // A hidden tool is refused on call; the inventory still answers.
        var denied = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "mcphub__check_hub_update" }, CancellationToken.None);
        Assert.True(denied.IsError);
        Assert.Equal(ToolCallOutcome.Denied, Assert.Single(sink.Events).Outcome);

        var listed = await handlers.CallToolAsync(TenantContext.Default,
            new CallToolRequestParams { Name = "mcphub__list_services" }, CancellationToken.None);
        Assert.NotEqual(true, listed.IsError);
        Assert.Contains("KodiMCPSharp", Text(listed));
    }

    [Fact]
    public void With_every_capability_off_only_the_inventory_remains()
    {
        using var f = new Fixture();
        f.Settings.Current.AgentManagementEnabled = true;
        f.Settings.Current.AgentManagementControlEnabled = false;
        f.Settings.Current.AgentManagementInstallEnabled = false;
        f.Settings.Current.AgentManagementUpdateChecksEnabled = false;

        Assert.Equal(["mcphub__list_services"], ManagementNames(f.Handlers()));
        Assert.True(f.Policy.IsToolEnabled("list_services"));
        Assert.False(f.Policy.IsToolEnabled("start"));
        Assert.False(f.Policy.IsToolEnabled("no_such_tool"));
    }

    [Fact]
    public void Environment_overrides_win_over_settings_and_are_reported()
    {
        using var f = new Fixture();

        // The override can force the feature ON when the persisted setting has it off …
        f.Env[AgentManagementPolicy.EnabledVariable] = "yes";
        Assert.True(f.Policy.ManagementEnabled);
        Assert.Equal("MCPHUB_AGENT_MANAGEMENT_ENABLED=yes", f.Policy.ManagementEnabledOverrideSource);
        Assert.Null(f.Policy.ControlOverrideSource);
        Assert.Equal(AllTools, ManagementNames(f.Handlers()));

        // … and pin individual capabilities off while the settings say on.
        f.Env[AgentManagementPolicy.InstallVariable] = " 0 ";
        f.Env[AgentManagementPolicy.ControlVariable] = "off";
        Assert.False(f.Policy.InstallEnabled);
        Assert.False(f.Policy.ControlEnabled);
        Assert.True(f.Policy.UpdateChecksEnabled);
        Assert.Equal("MCPHUB_AGENT_MANAGEMENT_INSTALL=0", f.Policy.InstallOverrideSource);
        Assert.Equal("MCPHUB_AGENT_MANAGEMENT_CONTROL=off", f.Policy.ControlOverrideSource);
        Assert.Null(f.Policy.UpdateChecksOverrideSource);
        Assert.Equal(["mcphub__list_services", .. UpdateCheckTools], ManagementNames(f.Handlers()));

        // Or force the whole feature OFF regardless of settings.
        f.Settings.Current.AgentManagementEnabled = true;
        f.Env[AgentManagementPolicy.EnabledVariable] = "false";
        Assert.False(f.Policy.ManagementEnabled);
        Assert.Empty(ManagementNames(f.Handlers()));

        // An unrecognised value is no override at all.
        f.Env[AgentManagementPolicy.EnabledVariable] = "maybe";
        Assert.True(f.Policy.ManagementEnabled);
        Assert.Null(f.Policy.ManagementEnabledOverrideSource);
    }

    [Fact]
    public void Capability_switches_are_kept_while_the_master_is_off()
    {
        using var f = new Fixture();
        f.Settings.Current.AgentManagementEnabled = false;
        f.Settings.Current.AgentManagementInstallEnabled = false;

        Assert.True(f.Policy.ControlSwitch, "the user's choice is kept for when management comes back on");
        Assert.False(f.Policy.InstallSwitch);
        Assert.True(f.Policy.UpdateChecksSwitch);
        Assert.False(f.Policy.ControlEnabled);
        Assert.False(f.Policy.UpdateChecksEnabled);
    }

    [Fact]
    public void Changes_take_effect_immediately_without_rebuilding_handlers()
    {
        using var f = new Fixture();
        var handlers = f.Handlers();

        Assert.Empty(ManagementNames(handlers));
        f.Settings.Current.AgentManagementEnabled = true;
        Assert.Equal(8, ManagementNames(handlers).Count);
        f.Settings.Current.AgentManagementInstallEnabled = false;
        Assert.Equal(6, ManagementNames(handlers).Count);
        f.Settings.Current.AgentManagementEnabled = false;
        Assert.Empty(ManagementNames(handlers));
    }

    [Fact]
    public void Stacks_with_the_recipes_policy_and_each_governs_only_its_own_tools()
    {
        using var f = new Fixture();
        var paths = new FakeAppPaths(Path.Combine(f.Manager.ServersFolder, "..", "recipes-home"));
        Directory.CreateDirectory(paths.SettingsDirectory);
        var recipeStore = new RecipeStore(paths, NullLogger<RecipeStore>.Instance);
        var recipePolicy = new RecipeAccessPolicy(f.Settings, name => f.Env.GetValueOrDefault(name));

        // Exactly the Composition.cs shape: both providers, both policies AND-ed together.
        var handlers = new ProxyHandlers(
            RegistryWith(("kodi", "kodi_list_instances")),
            new CompositeToolAuthorization(recipePolicy, f.Policy),
            null, null,
            [new RecipeToolProvider(recipeStore), f.Provider]);

        // Defaults: recipes on, management off.
        Assert.Contains("recipes__add", Names(handlers));
        Assert.Empty(ManagementNames(handlers));

        f.Settings.Current.AgentManagementEnabled = true;
        f.Settings.Current.RecipesEnabled = false;
        Assert.DoesNotContain(Names(handlers), n => n.StartsWith("recipes__", StringComparison.Ordinal));
        Assert.Equal(AllTools, ManagementNames(handlers));
        Assert.Contains("kodi__kodi_list_instances", Names(handlers));

        f.Settings.Current.RecipesEnabled = true;
        f.Settings.Current.AgentManagementControlEnabled = false;
        Assert.Equal(5, Names(handlers).Count(n => n.StartsWith("recipes__", StringComparison.Ordinal)));
        Assert.Equal(AllTools.Except(ControlTools), ManagementNames(handlers));
    }

    [Fact]
    public void Composite_authorization_requires_every_policy_to_agree()
    {
        var tenantGrants = Grants(("alice", ["kodi"]), ("bob", ["kodi", "mcphub"]));
        var composite = new CompositeToolAuthorization(AllowAllToolAuthorization.Instance, tenantGrants);

        Assert.True(composite.IsToolVisible(new TenantContext("alice"), "kodi", "kodi__x"));
        Assert.False(composite.IsToolVisible(new TenantContext("alice"), "mcphub", "mcphub__start"));
        Assert.True(composite.IsCallAllowed(new TenantContext("bob"), "mcphub", "mcphub__start"));

        var empty = new CompositeToolAuthorization();
        Assert.True(empty.IsToolVisible(TenantContext.Default, "anything", "anything__x"));
        Assert.True(empty.IsCallAllowed(TenantContext.Default, "anything", "anything__x"));

        Assert.Throws<ArgumentException>(() => new CompositeToolAuthorization(AllowAllToolAuthorization.Instance, null!));
    }

    [Fact]
    public async Task Settings_roundtrip_the_four_switches()
    {
        using var dir = new TempDir();
        var paths = new FakeAppPaths(dir.Path);

        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        Assert.False(store.Current.AgentManagementEnabled);
        Assert.True(store.Current.AgentManagementControlEnabled);
        Assert.True(store.Current.AgentManagementInstallEnabled);
        Assert.True(store.Current.AgentManagementUpdateChecksEnabled);
        store.Current.AgentManagementEnabled = true;
        store.Current.AgentManagementControlEnabled = false;
        store.Current.AgentManagementInstallEnabled = false;
        store.Current.AgentManagementUpdateChecksEnabled = false;
        await store.SaveAsync();

        var reloaded = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        Assert.True(reloaded.Current.AgentManagementEnabled);
        Assert.False(reloaded.Current.AgentManagementControlEnabled);
        Assert.False(reloaded.Current.AgentManagementInstallEnabled);
        Assert.False(reloaded.Current.AgentManagementUpdateChecksEnabled);
    }

    [Fact]
    public async Task Host_serves_management_tools_and_combined_instructions_over_http()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var f = new Fixture();
        f.Settings.Current.AgentManagementEnabled = true;
        FakeServiceManager.MarkInstalled(f.Manager["KodiMCPSharp"], "1.0.0");

        var recipePolicy = new RecipeAccessPolicy(f.Settings, name => f.Env.GetValueOrDefault(name));
        var instructions = string.Join("\n\n", recipePolicy.ServerInstructions, f.Policy.ServerInstructions);
        var host = new ProxyHost(
            new ProxyHandlers(new FakeRegistry(), new CompositeToolAuthorization(recipePolicy, f.Policy), null, null, [f.Provider]),
            NullLoggerFactory.Instance,
            new ProxyHostOptions { ServerInstructions = instructions });
        await host.StartAsync("127.0.0.1", port: 0, cts.Token);
        try
        {
            await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(
                new ModelContextProtocol.Client.HttpClientTransport(new ModelContextProtocol.Client.HttpClientTransportOptions
                {
                    Endpoint = new Uri(host.EndpointUrl),
                    TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
                }, NullLoggerFactory.Instance),
                clientOptions: null, NullLoggerFactory.Instance, cts.Token);

            Assert.Equal(instructions, client.ServerInstructions);
            Assert.Contains("mcphub__start", client.ServerInstructions);

            var tools = await client.ListToolsAsync(new ListToolsRequestParams(), cts.Token);
            Assert.Equal(AllTools, tools.Tools.Select(t => t.Name).Where(n => n.StartsWith("mcphub__", StringComparison.Ordinal)));
            var start = tools.Tools.Single(t => t.Name == "mcphub__start");
            Assert.StartsWith("[MCPHub] ", start.Description);
            Assert.Equal("service", start.InputSchema.GetProperty("required")[0].GetString());

            var listed = await client.CallToolAsync(new CallToolRequestParams { Name = "mcphub__list_services" }, cts.Token);
            Assert.NotEqual(true, listed.IsError);
            Assert.Contains("\"key\": \"kodi\"", Text(listed));

            var started = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "mcphub__start",
                Arguments = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["service"] = System.Text.Json.JsonSerializer.SerializeToElement("kodi"),
                    ["wait_seconds"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
                },
            }, cts.Token);
            Assert.NotEqual(true, started.IsError);
            Assert.Equal(["start:KodiMCPSharp"], f.Host.Calls);

            // Flip a switch while the host is up: the tool vanishes and a call is refused, no restart needed.
            f.Settings.Current.AgentManagementControlEnabled = false;
            var after = await client.ListToolsAsync(new ListToolsRequestParams(), cts.Token);
            Assert.DoesNotContain("mcphub__start", after.Tools.Select(t => t.Name));
            var refused = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "mcphub__stop",
                Arguments = new Dictionary<string, System.Text.Json.JsonElement> { ["service"] = System.Text.Json.JsonSerializer.SerializeToElement("kodi") },
            }, cts.Token);
            Assert.True(refused.IsError);
            Assert.Single(f.Host.Calls);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("Enabled", true)]
    [InlineData("disabled", false)]
    [InlineData("OFF", false)]
    [InlineData(null, null)]
    [InlineData("sometimes", null)]
    public void Environment_flags_share_one_parser_with_recipes(string? raw, bool? expected)
    {
        Assert.Equal(expected, EnvironmentFlag.Parse(raw));
        Assert.Equal(expected, RecipeAccessPolicy.ParseFlag(raw));
        Assert.Equal(expected is null ? null : $"X={raw!.Trim()}", EnvironmentFlag.Describe("X", raw));
    }
}
