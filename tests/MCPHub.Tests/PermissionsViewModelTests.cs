using CommunityToolkit.Mvvm.Messaging;
using MCPHub.App.Messages;
using MCPHub.App.ViewModels;
using MCPHub.Core.Management;
using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Core.Users;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// The Permissions page: which tools each user may use, and why a tool is withheld.
///
/// <para>What is pinned here is mostly about <em>not misleading an operator</em>: an editor that stays
/// open on the user being worked on, a warning when the selected user is suspended and its grants
/// therefore inert, and an explanation that lists every reason a tool is withheld rather than the
/// first. Users and keys are <see cref="UsersViewModelTests"/>'s.</para>
/// </summary>
public sealed class PermissionsViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed record Fixture(PermissionsViewModel Vm, UserStore Users, PermissionsStore Store);

    /// <summary>
    /// Two services and one of the hub's own groups. The picker shows what exists, so a test about
    /// picking tools has to say what exists.
    /// </summary>
    private static IToolCatalog Catalog() => new StubCatalog(
    [
        new ToolGroup("kodi", "Kodi", ToolGroupKind.Service, true, ["kodi__play_pause", "kodi__stop"]),
        new ToolGroup("redis", "Redis", ToolGroupKind.Service, true, ["redis__get", "redis__set"]),
        new ToolGroup("offline", "Offline service", ToolGroupKind.Service, false, []),
        new ToolGroup("recipes", "Recipes", ToolGroupKind.Hub, true, ["recipes__list"]),
    ]);

    private sealed class StubCatalog(IReadOnlyList<ToolGroup> groups) : IToolCatalog
    {
        public IReadOnlyList<ToolGroup> Groups => groups;
    }

    private static ToolGroupRow Group(PermissionsViewModel vm, string key) =>
        vm.Groups.Single(g => g.Key == key);

    private static ToolRow Tool(PermissionsViewModel vm, string name) =>
        vm.Groups.SelectMany(g => g.Tools).Single(t => t.Name == name);

    private Fixture Build(params (string Variable, string Value)[] environment)
    {
        var map = environment.ToDictionary(e => e.Variable, e => e.Value, StringComparer.Ordinal);
        string? Read(string name) => map.GetValueOrDefault(name);

        var paths = new FakeAppPaths(_dir.Path);
        var users = new UserStore(paths);
        var store = new PermissionsStore(paths);
        var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var authorization = new CompositeToolAuthorization(
            new PermissionsToolAuthorization(users, store),
            new AdministrationPolicy(settings, Read),
            new AgentManagementPolicy(settings, Read));

        return new Fixture(new PermissionsViewModel(users, store, authorization, Catalog()), users, store);
    }

    // ---- the list ------------------------------------------------------------------------------

    /// <summary>
    /// The page lists users, not grants: somebody with a key and no grants is the case an operator most
    /// needs to see, and a list of grant entries would leave them out entirely.
    /// </summary>
    [Fact]
    public void Every_user_is_listed_whether_or_not_it_holds_grants()
    {
        var f = Build();
        f.Users.Create("granted");
        f.Users.Create("ungranted");
        f.Store.SetGrants(f.Users.Snapshot.Users[0].Id, ["kodi__*"]);

        var reloaded = Build();

        Assert.Equal(2, reloaded.Vm.Rows.Count);
        Assert.True(reloaded.Vm.HasRows);
        Assert.False(reloaded.Vm.HasNoRows);
        Assert.Contains(reloaded.Vm.Rows, r => r.Tools.Length == 0);
    }

    [Fact]
    public void With_no_users_the_page_says_so_rather_than_showing_an_empty_grid()
    {
        var f = Build();

        Assert.True(f.Vm.HasNoRows);
        Assert.False(f.Vm.HasUserSelected);
    }

    /// <summary>
    /// And offers the way out as a button: this page can only name users, so "add a user" has to take
    /// somebody to where users are made rather than tell them to go and find the page.
    /// </summary>
    [Fact]
    public void Add_a_user_opens_the_users_page()
    {
        var f = Build();
        var asked = new List<string>();
        var subscriber = new object();
        WeakReferenceMessenger.Default.Register<ShowPageMessage>(subscriber, (_, m) => asked.Add(m.Title));

        try
        {
            f.Vm.OpenUsersCommand.Execute(null);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(subscriber);
        }

        Assert.Equal("Users", Assert.Single(asked));
    }

    /// <summary>
    /// While everyone is allowed, nothing on this page does anything: every caller already has every
    /// tool. The list underneath shows users looking carefully configured, which is exactly why it has
    /// to be said at the top rather than left to be discovered.
    /// </summary>
    [Fact]
    public void Everyone_being_allowed_is_warned_about_at_the_top()
    {
        var f = Build();

        Assert.True(f.Vm.HasPolicyWarning);
        Assert.Contains("every tool", f.Vm.PolicyWarning!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Users page", f.Vm.PolicyWarning!, StringComparison.Ordinal);

        f.Store.SetAllowUnauthenticated(false);
        f.Vm.RefreshPolicy();

        Assert.False(f.Vm.HasPolicyWarning);
    }

    /// <summary>The choice is made on another page, so coming back has to re-read it rather than show
    /// what was true when this page was last built.</summary>
    [Fact]
    public void The_warning_is_re_read_when_the_page_is_shown()
    {
        var f = Build();
        f.Store.SetAllowUnauthenticated(false);
        var reloaded = Build();
        Assert.False(reloaded.Vm.HasPolicyWarning);

        reloaded.Store.SetAllowUnauthenticated(true);
        reloaded.Vm.RefreshPolicy();

        Assert.True(reloaded.Vm.HasPolicyWarning);
    }

    // ---- editing grants ------------------------------------------------------------------------

    /// <summary>
    /// What a user holds arrives as ticks, whichever form it was written in: a whole service ticks the
    /// service and everything under it, one tool ticks just that tool.
    /// </summary>
    [Fact]
    public void Selecting_a_user_ticks_what_it_already_holds()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["kodi__*", "redis__get"]);
        var reloaded = Build();

        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();

        Assert.True(reloaded.Vm.HasUserSelected);
        Assert.Contains("Tools for agent", reloaded.Vm.EditorTitle, StringComparison.Ordinal);

        Assert.True(Group(reloaded.Vm, "kodi").IsGranted);
        Assert.All(Group(reloaded.Vm, "kodi").Tools, t => Assert.True(t.IsGranted));

        Assert.False(Group(reloaded.Vm, "redis").IsGranted);
        Assert.True(Tool(reloaded.Vm, "redis__get").IsGranted);
        Assert.False(Tool(reloaded.Vm, "redis__set").IsGranted);

        Assert.False(reloaded.Vm.GrantEverything);
        Assert.Empty(reloaded.Vm.KeptGrants);
    }

    /// <summary>Ticking a service grants the service, not the tools it happens to have today — so a
    /// tool it adds later is covered without anybody revisiting this page.</summary>
    [Fact]
    public void Ticking_a_service_saves_it_as_the_whole_service()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        Group(vm, "kodi").IsGranted = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal(["kodi__*"], r.Store.GrantsFor(issued.User.Id)!.Tools);
    }

    /// <summary>
    /// And ticking every tool of a service means the same thing, so switching between the two lists
    /// cannot quietly change what a user is allowed.
    /// </summary>
    [Fact]
    public void Ticking_every_tool_of_a_service_saves_the_service()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        Tool(vm, "kodi__play_pause").IsGranted = true;
        Tool(vm, "kodi__stop").IsGranted = true;

        Assert.True(Group(vm, "kodi").IsGranted);
        vm.SaveCommand.Execute(null);

        Assert.Equal(["kodi__*"], r.Store.GrantsFor(issued.User.Id)!.Tools);
    }

    [Fact]
    public void Unticking_one_tool_drops_the_service_to_the_tools_that_are_left()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["kodi__*"]);
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        Tool(vm, "kodi__stop").IsGranted = false;

        Assert.False(Group(vm, "kodi").IsGranted);
        vm.SaveCommand.Execute(null);

        Assert.Equal(["kodi__play_pause"], r.Store.GrantsFor(issued.User.Id)!.Tools);
    }

    /// <summary>
    /// Everything is its own grant, because it covers services that are not installed yet and no list
    /// of checkboxes can offer those. Ticking it ticks the lot; untying anything takes it back off.
    /// </summary>
    [Fact]
    public void Everything_is_saved_as_everything_and_comes_off_when_anything_is_unticked()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        vm.GrantEverything = true;
        Assert.All(vm.Groups, g => Assert.True(g.IsGranted));
        vm.SaveCommand.Execute(null);
        Assert.Equal(["*"], r.Store.GrantsFor(issued.User.Id)!.Tools);

        Tool(vm, "kodi__stop").IsGranted = false;

        Assert.False(vm.GrantEverything);
        vm.SaveCommand.Execute(null);
        Assert.DoesNotContain("*", r.Store.GrantsFor(issued.User.Id)!.Tools);
    }

    /// <summary>
    /// Saving must not close the editor: an operator adjusting grants on one user does several passes,
    /// and losing the selection after each one is the kind of thing that makes a page tiring to use.
    /// </summary>
    [Fact]
    public void Saving_keeps_the_user_selected_and_the_ticks_where_they_were()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        Group(vm, "kodi").IsGranted = true;
        Tool(vm, "redis__get").IsGranted = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal(["kodi__*", "redis__get"], r.Store.GrantsFor(issued.User.Id)!.Tools);
        Assert.NotNull(vm.SelectedUser);
        Assert.Equal(["kodi__*", "redis__get"], vm.SelectedUser!.Tools);
        Assert.True(Group(vm, "kodi").IsGranted);
        Assert.True(Tool(vm, "redis__get").IsGranted);
    }

    [Fact]
    public void Revoking_everything_leaves_the_user_holding_nothing()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["*"]);
        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();

        reloaded.Vm.RevokeAllCommand.Execute(null);

        Assert.Empty(reloaded.Store.GrantsFor(issued.User.Id)!.Tools);
        Assert.NotEmpty(reloaded.Vm.StatusMessage);
    }

    /// <summary>
    /// The picker cannot produce a grant that matches nothing, but the box for naming one by hand can,
    /// and the rules still refuse it rather than storing something that looks granted and is not.
    /// </summary>
    [Fact]
    public void A_hand_written_grant_that_could_never_match_is_reported_rather_than_saved()
    {
        var f = Build();
        f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        vm.NewGrant = "two words";
        vm.AddGrantCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Empty(r.Store.Snapshot.Grants);
        Assert.NotEmpty(vm.StatusMessage);
    }

    /// <summary>
    /// A grant naming something the hub cannot currently see — a service that is not connected, or one
    /// not installed yet — survives a save. A picker that can only show what exists must not delete
    /// what it cannot show.
    /// </summary>
    [Fact]
    public void Grants_the_picker_cannot_show_are_kept_through_a_save()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["notinstalled__*", "kodi__stop"]);
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        Assert.Equal(["notinstalled__*"], vm.KeptGrants);
        Assert.True(vm.HasKeptGrants);
        Assert.True(Tool(vm, "kodi__stop").IsGranted);

        vm.SaveCommand.Execute(null);

        Assert.Equal(["kodi__stop", "notinstalled__*"], r.Store.GrantsFor(issued.User.Id)!.Tools);
    }

    [Fact]
    public void A_hand_written_grant_the_picker_knows_is_ticked_rather_than_listed_twice()
    {
        var f = Build();
        f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        vm.NewGrant = "kodi__*";
        vm.AddGrantCommand.Execute(null);

        Assert.True(Group(vm, "kodi").IsGranted);
        Assert.Empty(vm.KeptGrants);
        Assert.Empty(vm.NewGrant);
    }

    [Fact]
    public void A_kept_grant_can_be_removed()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["notinstalled__*"]);
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        vm.RemoveGrantCommand.Execute("notinstalled__*");
        vm.SaveCommand.Execute(null);

        Assert.Empty(r.Store.GrantsFor(issued.User.Id)!.Tools);
        Assert.False(vm.HasKeptGrants);
    }

    // ---- working a long list -------------------------------------------------------------------

    /// <summary>
    /// Space is what the full list is worked with: select a run of rows, press it once. Mixed
    /// selections all go the same way, so one press has one outcome rather than inverting each row.
    /// </summary>
    [Fact]
    public void Space_ticks_a_whole_selection_and_a_second_press_unticks_it()
    {
        var f = Build();
        f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();
        var chosen = new[] { Tool(vm, "kodi__stop"), Tool(vm, "redis__get") };
        chosen[0].IsGranted = true;

        vm.ToggleTools(chosen);

        Assert.All(chosen, t => Assert.True(t.IsGranted));

        vm.ToggleTools(chosen);

        Assert.All(chosen, t => Assert.False(t.IsGranted));
    }

    [Fact]
    public void Select_all_and_select_none_cover_every_tool_on_show()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        vm.SelectAllToolsCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        // Every service ends up whole, since every one of its tools is ticked.
        Assert.Equal(["kodi__*", "recipes__*", "redis__*"], r.Store.GrantsFor(issued.User.Id)!.Tools.Order());

        vm.SelectNoToolsCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Empty(r.Store.GrantsFor(issued.User.Id)!.Tools);
    }

    /// <summary>
    /// With a filter on, "select all" means the matches. Anything else makes a search box dangerous:
    /// you would be granting what you cannot see.
    /// </summary>
    [Fact]
    public void Select_all_inside_a_filter_only_touches_what_is_showing()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        vm.ToolFilter = "redis";
        Assert.Equal(["redis__get", "redis__set"], vm.VisibleTools.Select(t => t.Name));

        vm.SelectAllToolsCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Equal(["redis__*"], r.Store.GrantsFor(issued.User.Id)!.Tools);
    }

    [Fact]
    public void The_filter_matches_a_service_name_as_well_as_a_tool_name()
    {
        var f = Build();
        f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        vm.ToolFilter = "Kodi";

        Assert.Equal(["kodi__play_pause", "kodi__stop"], vm.VisibleTools.Select(t => t.Name));
    }

    /// <summary>A service that is not connected has no tools to tick, so the whole-service checkbox is
    /// the only way to grant it — and it still works.</summary>
    [Fact]
    public void A_service_with_no_connection_can_still_be_granted_whole()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var r = Build();
        var vm = r.Vm;
        vm.SelectedUser = vm.Rows.Single();

        var offline = Group(vm, "offline");
        Assert.False(offline.IsAvailable);
        Assert.Empty(offline.Tools);

        offline.IsGranted = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal(["offline__*"], r.Store.GrantsFor(issued.User.Id)!.Tools);
    }

    /// <summary>
    /// Grants on a suspended user are real and apply to nothing. The page that edits them has to say
    /// so, or somebody changes a grant, sees no effect, and goes looking in the wrong place.
    /// </summary>
    [Fact]
    public void A_suspended_user_is_flagged_on_the_page_that_edits_its_grants()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Users.SetEnabled(issued.User.Id, false);
        var reloaded = Build();

        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();

        Assert.True(reloaded.Vm.HasSelectedUserWarning);
        Assert.Contains("suspended", reloaded.Vm.SelectedUserWarning!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Nothing is granted while the document cannot be read, and every save throws, so the
    /// page says it outright rather than letting somebody edit into an exception.</summary>
    [Fact]
    public void A_document_that_cannot_be_read_is_reported_on_the_page()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "permissions.json"), "{ not json");

        var f = Build();

        Assert.True(f.Vm.HasConfigurationError);
        Assert.NotNull(f.Vm.ConfigurationError);
    }

    // ---- explaining ----------------------------------------------------------------------------

    [Fact]
    public void Explain_needs_a_user_and_a_tool_before_it_says_anything()
    {
        var f = Build();

        f.Vm.ExplainCommand.Execute(null);
        Assert.Contains("user", f.Vm.ExplainSummary, StringComparison.OrdinalIgnoreCase);

        f.Users.Create("agent");
        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();
        reloaded.Vm.ExplainCommand.Execute(null);
        Assert.Contains("tool", reloaded.Vm.ExplainSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_says_plainly_when_a_tool_is_available()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["kodi__*"]);
        f.Store.SetAllowUnauthenticated(false);

        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();
        reloaded.Vm.ExplainTool = "kodi__play_pause";

        reloaded.Vm.ExplainCommand.Execute(null);

        Assert.Empty(reloaded.Vm.Denials);
        Assert.False(reloaded.Vm.HasDenials);
        Assert.True(reloaded.Vm.HasExplanation);
        Assert.Contains("can use", reloaded.Vm.ExplainSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page's reason for existing: a tool withheld by two independent things at once, both named,
    /// with the environment variable that is pinning one of them.
    /// </summary>
    [Fact]
    public void Explain_lists_every_reason_a_tool_is_withheld()
    {
        var environment = new[]
        {
            (AgentManagementPolicy.EnabledVariable, "true"),
            (AgentManagementPolicy.InstallVariable, "false"),
        };

        var f = Build(environment);
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["kodi__*"]);
        f.Store.SetAllowUnauthenticated(false);

        var reloaded = Build(environment);
        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();
        reloaded.Vm.ExplainTool = "mcphub__install";

        reloaded.Vm.ExplainCommand.Execute(null);

        Assert.Equal(2, reloaded.Vm.Denials.Count);
        Assert.Contains(reloaded.Vm.Denials, d => d.Code == PermissionsToolAuthorization.NoGrantCode);

        var pinned = reloaded.Vm.Denials.Single(d => d.Code == AgentManagementPolicy.CapabilityOffCode);
        Assert.True(pinned.IsPinned);
        Assert.Contains(AgentManagementPolicy.InstallVariable, pinned.PinnedBy, StringComparison.Ordinal);
        Assert.True(pinned.HasRemedy);

        // And it says how many there are, so nobody fixes one and assumes they are done.
        Assert.Contains("2 reason", reloaded.Vm.ExplainSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Suspension is reported as the reason, not the grants the user still holds — the operator's next
    /// move is on the Users page, and a denial about grants would send them to the wrong one.
    /// </summary>
    [Fact]
    public void Explain_blames_suspension_rather_than_the_grants_still_in_place()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["*"]);
        f.Store.SetAllowUnauthenticated(false);
        f.Users.SetEnabled(issued.User.Id, false);

        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();
        reloaded.Vm.ExplainTool = "kodi__play_pause";

        reloaded.Vm.ExplainCommand.Execute(null);

        var denial = Assert.Single(reloaded.Vm.Denials);
        Assert.Equal(PermissionsToolAuthorization.UserDisabledCode, denial.Code);
    }
}
