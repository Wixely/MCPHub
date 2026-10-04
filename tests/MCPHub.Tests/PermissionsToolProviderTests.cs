using System.Text.Json;
using MCPHub.Core.Management;
using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// Hub policy as an API: reading it, editing it, and explaining it.
///
/// <para>The explanation tool is the one worth the most here. A management UI in another process
/// cannot see a settings file or a container's environment, so anything it cannot be told it cannot
/// show — and "the tool is missing" with no reason is the state this whole layer exists to end.</para>
/// </summary>
public sealed class PermissionsToolProviderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private PermissionsStore Store() => new(new FakeAppPaths(_dir.Path));

    private PermissionsToolProvider Provider(
        IPermissionsConfigurationSource source, params IToolAuthorization[] policies) =>
        new(source,
            new CompositeToolAuthorization(policies.Length > 0 ? policies : [new PermissionsToolAuthorization(source)]),
            NullLogger<PermissionsToolProvider>.Instance);

    private AgentManagementPolicy Management(params (string Variable, string Value)[] environment)
    {
        var map = environment.ToDictionary(e => e.Variable, e => e.Value, StringComparer.Ordinal);
        return new AgentManagementPolicy(
            new SettingsStore(new FakeAppPaths(_dir.Path), NullLogger<SettingsStore>.Instance),
            name => map.GetValueOrDefault(name));
    }

    private static async Task<JsonElement> CallAsync(
        PermissionsToolProvider provider, string tool, object? arguments = null)
    {
        var result = await provider.CallAsync(tool, Arguments(arguments), CancellationToken.None);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(bool IsError, JsonElement Body)> TryCallAsync(
        PermissionsToolProvider provider, string tool, object? arguments = null)
    {
        var result = await provider.CallAsync(tool, Arguments(arguments), CancellationToken.None);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return (result.IsError ?? false, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static Dictionary<string, JsonElement>? Arguments(object? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(arguments);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
    }

    // ---- reading --------------------------------------------------------------------------------

    [Fact]
    public async Task Status_says_whether_policy_can_be_edited_here()
    {
        var editable = await CallAsync(Provider(Store()), "status");
        Assert.True(editable.GetProperty("editable").GetBoolean());
        Assert.True(editable.GetProperty("allowUnauthenticated").GetBoolean());
        Assert.Equal(0, editable.GetProperty("principalCount").GetInt32());
    }

    [Fact]
    public async Task Listing_principals_never_includes_a_key()
    {
        var store = Store();
        var (_, key) = store.CreatePrincipal("Banter", ["*"]);

        var listed = await CallAsync(Provider(store), "list_principals");

        Assert.DoesNotContain(key, listed.ToString(), StringComparison.Ordinal);
        var principal = Assert.Single(listed.GetProperty("principals").EnumerateArray().ToList());
        Assert.Equal("Banter", principal.GetProperty("name").GetString());
        Assert.Equal(8, principal.GetProperty("keyFingerprint").GetString()!.Length);
    }

    // ---- issuing keys ---------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_principal_hands_back_a_working_key_once()
    {
        var store = Store();
        var provider = Provider(store);

        var created = await CallAsync(provider, "create_principal", new { name = "Banter", tools = new[] { "*" } });
        var key = created.GetProperty("key").GetString()!;

        Assert.NotEmpty(key);
        Assert.NotEmpty(created.GetProperty("notice").GetString());
        Assert.Equal("Banter", store.Resolve(key)!.Name);

        // And nothing else will ever give it back.
        Assert.DoesNotContain(key, (await CallAsync(provider, "list_principals")).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotating_a_key_retires_the_previous_one()
    {
        var store = Store();
        var provider = Provider(store);
        var first = (await CallAsync(provider, "create_principal", new { name = "Banter" }))
            .GetProperty("key").GetString()!;

        var second = (await CallAsync(provider, "rotate_key", new { principal = "Banter" }))
            .GetProperty("key").GetString()!;

        Assert.Null(store.Resolve(first));
        Assert.NotNull(store.Resolve(second));
    }

    // ---- editing --------------------------------------------------------------------------------

    [Fact]
    public async Task Grants_can_be_replaced_and_the_reply_shows_what_they_now_are()
    {
        var store = Store();
        var provider = Provider(store);
        var key = (await CallAsync(provider, "create_principal", new { name = "agent", tools = new[] { "kodi__*" } }))
            .GetProperty("key").GetString()!;

        var changed = await CallAsync(
            provider, "set_grants", new { principal = "agent", tools = new[] { "redis__get" } });

        Assert.Equal(["redis__get"], store.Resolve(key)!.Tools);
        Assert.Equal(["redis__get"],
            changed.GetProperty("principal").GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    [Fact]
    public async Task Suspending_a_principal_stops_its_key_and_says_so()
    {
        var store = Store();
        var provider = Provider(store);
        var key = (await CallAsync(provider, "create_principal", new { name = "agent", tools = new[] { "*" } }))
            .GetProperty("key").GetString()!;

        await CallAsync(provider, "set_enabled", new { principal = "agent", enabled = false });

        Assert.Null(store.Resolve(key));
        Assert.False((await CallAsync(provider, "list_principals"))
            .GetProperty("principals")[0].GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task The_unauthenticated_switch_can_be_turned_off_through_the_api()
    {
        var store = Store();

        await CallAsync(Provider(store), "set_allow_unauthenticated", new { allowed = false });

        Assert.False(store.Snapshot.AllowUnauthenticated);
    }

    // ---- errors a caller can act on -------------------------------------------------------------

    /// <summary>
    /// The failure most likely to waste an afternoon: a UI issues a grant against a container whose
    /// policy is a mounted file, the call looks like it worked, and nothing changes. It has to be a
    /// named refusal so the UI can grey the controls instead.
    /// </summary>
    [Fact]
    public async Task Editing_a_read_only_policy_is_refused_by_name_rather_than_appearing_to_work()
    {
        var mounted = new StaticPermissionsSource(new PermissionsConfiguration());
        var provider = Provider(mounted);

        var (isError, body) = await TryCallAsync(provider, "create_principal", new { name = "nope" });

        Assert.True(isError);
        Assert.Equal(PermissionsToolProvider.ReadOnlyCode, body.GetProperty("code").GetString());
        Assert.NotNull(body.GetProperty("remedy").GetString());

        // And status says the same thing up front, so a UI never has to find out by failing.
        Assert.False((await CallAsync(provider, "status")).GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_principal_is_refused_by_name()
    {
        var (isError, body) = await TryCallAsync(Provider(Store()), "rotate_key", new { principal = "ghost" });

        Assert.True(isError);
        Assert.Equal(PermissionsToolProvider.NoSuchPrincipalCode, body.GetProperty("code").GetString());
    }

    /// <summary>
    /// Two principals sharing a name must not be resolved arbitrarily: editing the wrong one and
    /// reporting success is worse than refusing.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_name_is_refused_rather_than_guessed()
    {
        var store = Store();
        store.CreatePrincipal("agent", []);
        store.CreatePrincipal("agent", []);

        var (isError, body) = await TryCallAsync(Provider(store), "set_enabled", new { principal = "agent", enabled = false });

        Assert.True(isError);
        Assert.Equal(PermissionsToolProvider.AmbiguousPrincipalCode, body.GetProperty("code").GetString());
        Assert.Contains("id", body.GetProperty("remedy").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_grant_that_could_never_match_is_refused_with_a_code()
    {
        var store = Store();
        store.CreatePrincipal("agent", []);

        var (isError, body) = await TryCallAsync(
            Provider(store), "set_grants", new { principal = "agent", tools = new[] { "two words" } });

        Assert.True(isError);
        Assert.Equal("permissions.invalid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_missing_argument_is_refused_with_a_code()
    {
        var (isError, body) = await TryCallAsync(Provider(Store()), "set_enabled", new { principal = "x" });

        Assert.True(isError);
        Assert.Equal("permissions.bad_argument", body.GetProperty("code").GetString());
    }

    // ---- explaining -----------------------------------------------------------------------------

    [Fact]
    public async Task Explain_says_a_granted_tool_is_available_with_no_reasons()
    {
        var store = Store();
        store.CreatePrincipal("agent", ["kodi__*"]);

        var explained = await CallAsync(
            Provider(store), "explain", new { principal = "agent", tool = "kodi__play_pause" });

        Assert.True(explained.GetProperty("available").GetBoolean());
        Assert.Empty(explained.GetProperty("denials").EnumerateArray().ToList());
        Assert.Equal("kodi", explained.GetProperty("serverKey").GetString());
    }

    [Fact]
    public async Task Explain_names_a_missing_grant_and_what_to_add()
    {
        var store = Store();
        store.CreatePrincipal("agent", ["kodi__*"]);

        var explained = await CallAsync(Provider(store), "explain", new { principal = "agent", tool = "redis__get" });

        Assert.False(explained.GetProperty("available").GetBoolean());
        var denial = Assert.Single(explained.GetProperty("denials").EnumerateArray().ToList());
        Assert.Equal(PermissionsToolAuthorization.NoGrantCode, denial.GetProperty("code").GetString());
        Assert.Contains("redis__*", denial.GetProperty("remedy").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case this tool exists for. Grant it and nothing happens, because a switch is off; turn the
    /// switch on and nothing happens, because the grant was missing. One reason at a time makes each
    /// fix look like a failure — so both arrive at once, and the pinning variable with them.
    /// </summary>
    [Fact]
    public async Task Explain_reports_both_causes_when_a_grant_and_a_switch_are_both_withholding_a_tool()
    {
        var store = Store();
        store.SetAllowUnauthenticated(false);
        store.CreatePrincipal("agent", ["kodi__*"]);

        var provider = Provider(
            store,
            new PermissionsToolAuthorization(store),
            Management((AgentManagementPolicy.EnabledVariable, "true"), (AgentManagementPolicy.InstallVariable, "false")));

        var explained = await CallAsync(provider, "explain", new { principal = "agent", tool = "mcphub__install" });
        var denials = explained.GetProperty("denials").EnumerateArray().ToList();

        Assert.Equal(2, denials.Count);
        Assert.Contains(denials, d => d.GetProperty("code").GetString() == PermissionsToolAuthorization.NoGrantCode);

        var switchDenial = denials.Single(d => d.GetProperty("code").GetString() == AgentManagementPolicy.CapabilityOffCode);
        Assert.Contains(
            AgentManagementPolicy.InstallVariable,
            switchDenial.GetProperty("pinnedBy").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And fixing the grant leaves exactly the switch, which is how an operator working through a
    /// problem knows the first change landed.
    /// </summary>
    [Fact]
    public async Task Explain_narrows_as_each_cause_is_fixed()
    {
        var store = Store();
        store.SetAllowUnauthenticated(false);
        var principal = store.CreatePrincipal("agent", []).Principal;

        var provider = Provider(
            store,
            new PermissionsToolAuthorization(store),
            Management((AgentManagementPolicy.EnabledVariable, "false")));

        store.SetGrants(principal.Id, ["mcphub__*"]);
        var denials = (await CallAsync(provider, "explain", new { principal = "agent", tool = "mcphub__install" }))
            .GetProperty("denials").EnumerateArray().ToList();

        var remaining = Assert.Single(denials);
        Assert.Equal(AgentManagementPolicy.ManagementOffCode, remaining.GetProperty("code").GetString());
    }

    /// <summary>
    /// The permissions tools are gated by their own switch, off by default, so that the widest tool
    /// grant does not quietly amount to administrator.
    /// </summary>
    [Fact]
    public async Task Explain_reports_the_permissions_switch_being_off()
    {
        var store = Store();
        store.SetAllowUnauthenticated(false);
        store.CreatePrincipal("agent", ["*"]);
        var settings = new SettingsStore(new FakeAppPaths(_dir.Path), NullLogger<SettingsStore>.Instance);

        var provider = Provider(
            store,
            new PermissionsToolAuthorization(store),
            new PermissionsManagementPolicy(settings, _ => null));

        var explained = await CallAsync(
            provider, "explain", new { principal = "agent", tool = "permissions__set_grants" });

        Assert.False(explained.GetProperty("available").GetBoolean());
        var denial = Assert.Single(explained.GetProperty("denials").EnumerateArray().ToList());
        Assert.Equal(PermissionsManagementPolicy.ManagementOffCode, denial.GetProperty("code").GetString());
        Assert.Contains(
            PermissionsManagementPolicy.EnabledVariable,
            denial.GetProperty("remedy").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_refuses_a_principal_it_cannot_find()
    {
        var (isError, body) = await TryCallAsync(
            Provider(Store()), "explain", new { principal = "ghost", tool = "kodi__play_pause" });

        Assert.True(isError);
        Assert.Equal(PermissionsToolProvider.NoSuchPrincipalCode, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_tool_name_is_refused_with_a_code()
    {
        var (isError, body) = await TryCallAsync(Provider(Store()), "not_a_tool");

        Assert.True(isError);
        Assert.Equal("permissions.unknown_tool", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Every_declared_tool_is_implemented()
    {
        var provider = Provider(Store());

        foreach (var tool in provider.Tools)
        {
            var (_, body) = await TryCallAsync(provider, tool.Name);
            Assert.NotEqual("permissions.unknown_tool", body.TryGetProperty("code", out var code) ? code.GetString() : null);
        }
    }
}
