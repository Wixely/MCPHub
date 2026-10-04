using System.Text.Json;
using MCPHub.Core.Management;
using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Core.Users;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// Hub grants as an API: reading them, editing them, and explaining them.
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

    private UserStore Users() => new(new FakeAppPaths(_dir.Path));

    private PermissionsToolProvider Provider(
        IUserDirectory users, IPermissionsConfigurationSource source, params IToolAuthorization[] policies) =>
        new(users,
            source,
            new CompositeToolAuthorization(
                policies.Length > 0 ? policies : [new PermissionsToolAuthorization(users, source)]),
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
        Assert.False(result.IsError ?? false, text);
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
        var status = await CallAsync(Provider(Users(), Store()), "status");

        Assert.True(status.GetProperty("editable").GetBoolean());
        Assert.True(status.GetProperty("allowUnauthenticated").GetBoolean());
        Assert.False(status.GetProperty("keysEnforced").GetBoolean());
        Assert.Equal(0, status.GetProperty("userCount").GetInt32());
    }

    /// <summary>
    /// The configuration that looks right and does nothing: keys issued while unauthenticated callers
    /// are still allowed everything. Status has to say so, because no single tool call looks wrong.
    /// </summary>
    [Fact]
    public async Task Status_warns_when_users_exist_but_their_keys_are_not_enforced()
    {
        var users = Users();
        users.Create("Banter");

        var status = await CallAsync(Provider(users, Store()), "status");

        Assert.Equal(1, status.GetProperty("userCount").GetInt32());
        Assert.NotNull(status.GetProperty("warning").GetString());

        var enforced = Store();
        enforced.SetAllowUnauthenticated(false);
        var after = await CallAsync(Provider(users, enforced), "status");

        Assert.True(after.GetProperty("keysEnforced").GetBoolean());
        Assert.False(after.TryGetProperty("warning", out _));
    }

    [Fact]
    public async Task Listing_grants_names_the_user_and_never_a_key()
    {
        var users = Users();
        var issued = users.Create("Banter");
        var store = Store();
        store.SetGrants(issued.User.Id, ["*"]);

        var listed = await CallAsync(Provider(users, store), "list_grants");

        Assert.DoesNotContain(issued.Key, listed.ToString(), StringComparison.Ordinal);
        var grant = Assert.Single(listed.GetProperty("grants").EnumerateArray().ToList());
        Assert.Equal("Banter", grant.GetProperty("userName").GetString());
        Assert.True(grant.GetProperty("userExists").GetBoolean());
        Assert.Equal(["*"], grant.GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).ToArray());
    }

    /// <summary>
    /// A grant on a user that has gone applies to nothing, and an operator reading the list should be
    /// able to see that rather than believe somebody still holds it.
    /// </summary>
    [Fact]
    public async Task A_grant_left_behind_by_a_deleted_user_is_listed_as_not_existing()
    {
        var store = Store();
        store.SetGrants("ghost", ["*"]);

        var grant = Assert.Single(
            (await CallAsync(Provider(Users(), store), "list_grants")).GetProperty("grants").EnumerateArray().ToList());

        Assert.False(grant.GetProperty("userExists").GetBoolean());
        Assert.False(grant.GetProperty("userEnabled").GetBoolean());
    }

    // ---- editing --------------------------------------------------------------------------------

    [Fact]
    public async Task Grants_can_be_replaced_and_the_reply_shows_what_they_now_are()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetGrants(issued.User.Id, ["kodi__*"]);
        var provider = Provider(users, store);

        var changed = await CallAsync(provider, "set_grants", new { user = "agent", tools = new[] { "redis__get" } });

        Assert.Equal(["redis__get"], store.GrantsFor(issued.User.Id)!.Tools);
        Assert.Equal(
            ["redis__get"],
            changed.GetProperty("grant").GetProperty("tools").EnumerateArray().Select(t => t.GetString()!).ToArray());
    }

    [Fact]
    public async Task A_user_can_be_addressed_by_id_as_well_as_by_name()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();

        await CallAsync(Provider(users, store), "set_grants", new { user = issued.User.Id, tools = new[] { "*" } });

        Assert.Equal(["*"], store.GrantsFor(issued.User.Id)!.Tools);
    }

    [Fact]
    public async Task An_empty_grant_list_revokes_everything()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetGrants(issued.User.Id, ["*"]);

        await CallAsync(Provider(users, store), "set_grants", new { user = "agent", tools = Array.Empty<string>() });

        Assert.Empty(store.GrantsFor(issued.User.Id)!.Tools);
    }

    /// <summary>
    /// What clear_grants is for: tidying an entry whose user is gone. Insisting the user exist would
    /// refuse exactly the case the tool was added for.
    /// </summary>
    [Fact]
    public async Task Grants_left_behind_by_a_deleted_user_can_be_cleared_by_id()
    {
        var store = Store();
        store.SetGrants("ghost", ["*"]);

        await CallAsync(Provider(Users(), store), "clear_grants", new { user = "ghost" });

        Assert.Empty(store.Snapshot.Grants);
    }

    [Fact]
    public async Task The_unauthenticated_switch_can_be_turned_off_through_the_api()
    {
        var store = Store();

        await CallAsync(Provider(Users(), store), "set_allow_unauthenticated", new { allowed = false });

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
        var users = Users();
        users.Create("agent");
        var provider = Provider(users, new StaticPermissionsSource(new PermissionsConfiguration()));

        var (isError, body) = await TryCallAsync(provider, "set_grants", new { user = "agent", tools = new[] { "*" } });

        Assert.True(isError);
        Assert.Equal(PermissionsToolProvider.ReadOnlyCode, body.GetProperty("code").GetString());
        Assert.NotNull(body.GetProperty("remedy").GetString());

        // And status says the same thing up front, so a UI never has to find out by failing.
        Assert.False((await CallAsync(provider, "status")).GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_user_is_refused_by_name()
    {
        var (isError, body) = await TryCallAsync(
            Provider(Users(), Store()), "set_grants", new { user = "ghost", tools = new[] { "*" } });

        Assert.True(isError);
        Assert.Equal(PermissionsToolProvider.NoSuchUserCode, body.GetProperty("code").GetString());
    }

    /// <summary>
    /// Two users sharing a name must not be resolved arbitrarily: granting the wrong one and reporting
    /// success is worse than refusing.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_name_is_refused_rather_than_guessed()
    {
        var users = Users();
        users.Create("agent");
        users.Create("agent");

        var (isError, body) = await TryCallAsync(
            Provider(users, Store()), "set_grants", new { user = "agent", tools = new[] { "*" } });

        Assert.True(isError);
        Assert.Equal(PermissionsToolProvider.AmbiguousUserCode, body.GetProperty("code").GetString());
        Assert.Contains("id", body.GetProperty("remedy").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_grant_that_could_never_match_is_refused_with_a_code()
    {
        var users = Users();
        users.Create("agent");

        var (isError, body) = await TryCallAsync(
            Provider(users, Store()), "set_grants", new { user = "agent", tools = new[] { "two words" } });

        Assert.True(isError);
        Assert.Equal("permissions.invalid", body.GetProperty("code").GetString());
    }

    /// <summary>
    /// A malformed call is reported as malformed even when the user is also wrong — answering "no such
    /// user" to somebody who forgot an argument sends them after the wrong mistake.
    /// </summary>
    [Fact]
    public async Task A_missing_argument_is_refused_before_the_user_is_looked_up()
    {
        var (isError, body) = await TryCallAsync(Provider(Users(), Store()), "set_grants", new { user = "ghost" });

        Assert.True(isError);
        Assert.Equal("permissions.bad_argument", body.GetProperty("code").GetString());
    }

    // ---- explaining -----------------------------------------------------------------------------

    [Fact]
    public async Task Explain_says_a_granted_tool_is_available_with_no_reasons()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetGrants(issued.User.Id, ["kodi__*"]);

        var explained = await CallAsync(
            Provider(users, store), "explain", new { user = "agent", tool = "kodi__play_pause" });

        Assert.True(explained.GetProperty("available").GetBoolean());
        Assert.Empty(explained.GetProperty("denials").EnumerateArray().ToList());
        Assert.Equal("kodi", explained.GetProperty("serverKey").GetString());
    }

    [Fact]
    public async Task Explain_names_a_missing_grant_and_what_to_add()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetGrants(issued.User.Id, ["kodi__*"]);

        var explained = await CallAsync(Provider(users, store), "explain", new { user = "agent", tool = "redis__get" });

        Assert.False(explained.GetProperty("available").GetBoolean());
        var denial = Assert.Single(explained.GetProperty("denials").EnumerateArray().ToList());
        Assert.Equal(PermissionsToolAuthorization.NoGrantCode, denial.GetProperty("code").GetString());
        Assert.Contains("redis__*", denial.GetProperty("remedy").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Suspension is the one switch that stops everything, so it has to be the reason reported rather
    /// than the grants that are still sitting there intact.
    /// </summary>
    [Fact]
    public async Task Explain_blames_suspension_rather_than_the_grants_a_suspended_user_still_holds()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetGrants(issued.User.Id, ["*"]);
        users.SetEnabled(issued.User.Id, false);

        var explained = await CallAsync(
            Provider(users, store), "explain", new { user = "agent", tool = "kodi__play_pause" });

        var denial = Assert.Single(explained.GetProperty("denials").EnumerateArray().ToList());
        Assert.Equal(PermissionsToolAuthorization.UserDisabledCode, denial.GetProperty("code").GetString());
    }

    /// <summary>
    /// The case this tool exists for. Grant it and nothing happens, because a switch is off; turn the
    /// switch on and nothing happens, because the grant was missing. One reason at a time makes each
    /// fix look like a failure — so both arrive at once, and the pinning variable with them.
    /// </summary>
    [Fact]
    public async Task Explain_reports_both_causes_when_a_grant_and_a_switch_are_both_withholding_a_tool()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetAllowUnauthenticated(false);
        store.SetGrants(issued.User.Id, ["kodi__*"]);

        var provider = Provider(
            users,
            store,
            new PermissionsToolAuthorization(users, store),
            Management(
                (AgentManagementPolicy.EnabledVariable, "true"), (AgentManagementPolicy.InstallVariable, "false")));

        var explained = await CallAsync(provider, "explain", new { user = "agent", tool = "mcphub__install" });
        var denials = explained.GetProperty("denials").EnumerateArray().ToList();

        Assert.Equal(2, denials.Count);
        Assert.Contains(denials, d => d.GetProperty("code").GetString() == PermissionsToolAuthorization.NoGrantCode);

        var switchDenial = denials.Single(
            d => d.GetProperty("code").GetString() == AgentManagementPolicy.CapabilityOffCode);
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
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetAllowUnauthenticated(false);

        var provider = Provider(
            users,
            store,
            new PermissionsToolAuthorization(users, store),
            Management((AgentManagementPolicy.EnabledVariable, "false")));

        store.SetGrants(issued.User.Id, ["mcphub__*"]);
        var denials = (await CallAsync(provider, "explain", new { user = "agent", tool = "mcphub__install" }))
            .GetProperty("denials").EnumerateArray().ToList();

        var remaining = Assert.Single(denials);
        Assert.Equal(AgentManagementPolicy.ManagementOffCode, remaining.GetProperty("code").GetString());
    }

    /// <summary>
    /// The administration tools are gated by their own switch, off by default, so that the widest tool
    /// grant does not quietly amount to administrator.
    /// </summary>
    [Fact]
    public async Task Explain_reports_the_administration_switch_being_off()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetAllowUnauthenticated(false);
        store.SetGrants(issued.User.Id, ["*"]);
        var settings = new SettingsStore(new FakeAppPaths(_dir.Path), NullLogger<SettingsStore>.Instance);

        var provider = Provider(
            users,
            store,
            new PermissionsToolAuthorization(users, store),
            new AdministrationPolicy(settings, _ => null));

        var explained = await CallAsync(provider, "explain", new { user = "agent", tool = "permissions__set_grants" });

        Assert.False(explained.GetProperty("available").GetBoolean());
        var denial = Assert.Single(explained.GetProperty("denials").EnumerateArray().ToList());
        Assert.Equal(AdministrationPolicy.AdministrationOffCode, denial.GetProperty("code").GetString());
        Assert.Contains(
            AdministrationPolicy.EnabledVariable,
            denial.GetProperty("remedy").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>And the same switch covers the users tools, since issuing a key is the other half of
    /// being able to grant oneself anything.</summary>
    [Fact]
    public async Task The_administration_switch_covers_the_users_tools_as_well()
    {
        var users = Users();
        var issued = users.Create("agent");
        var store = Store();
        store.SetAllowUnauthenticated(false);
        store.SetGrants(issued.User.Id, ["*"]);
        var settings = new SettingsStore(new FakeAppPaths(_dir.Path), NullLogger<SettingsStore>.Instance);

        var provider = Provider(
            users,
            store,
            new PermissionsToolAuthorization(users, store),
            new AdministrationPolicy(settings, _ => null));

        var explained = await CallAsync(provider, "explain", new { user = "agent", tool = "users__create" });

        Assert.False(explained.GetProperty("available").GetBoolean());
        Assert.Equal(
            AdministrationPolicy.AdministrationOffCode,
            explained.GetProperty("denials")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Explain_refuses_a_user_it_cannot_find()
    {
        var (isError, body) = await TryCallAsync(
            Provider(Users(), Store()), "explain", new { user = "ghost", tool = "kodi__play_pause" });

        Assert.True(isError);
        Assert.Equal(PermissionsToolProvider.NoSuchUserCode, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_tool_name_is_refused_with_a_code()
    {
        var (isError, body) = await TryCallAsync(Provider(Users(), Store()), "not_a_tool");

        Assert.True(isError);
        Assert.Equal("permissions.unknown_tool", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Every_declared_tool_is_implemented()
    {
        var provider = Provider(Users(), Store());

        foreach (var tool in provider.Tools)
        {
            var (_, body) = await TryCallAsync(provider, tool.Name);
            Assert.NotEqual(
                "permissions.unknown_tool", body.TryGetProperty("code", out var code) ? code.GetString() : null);
        }
    }
}
