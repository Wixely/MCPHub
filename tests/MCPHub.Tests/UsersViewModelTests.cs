using MCPHub.App.Infrastructure;
using MCPHub.App.ViewModels;
using MCPHub.Core.Permissions;
using MCPHub.Core.Routing;
using MCPHub.Core.Settings;
using MCPHub.Core.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// The Users page: one identity and one key per caller, issued here and selected everywhere else.
///
/// <para>Users exist as their own page because a key issued on the Permissions page and a key issued
/// on the Router page could never be the same key, so a caller needing both had to hold two. What is
/// pinned here is the consequence: a key shown once, a suspension that stops everything, and a removal
/// that takes the grants with it.</para>
/// </summary>
public sealed class UsersViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>
    /// Answers the page's confirmations and keeps what it was asked, so a test can check that the
    /// warning a person reads before everything changes actually says what it should.
    /// </summary>
    private sealed class StubConfirmation(bool answer) : IConfirmation
    {
        public List<ConfirmRequest> Asked { get; } = [];

        public ConfirmRequest Last => Assert.Single(Asked);

        public Task<bool> AskAsync(ConfirmRequest request)
        {
            Asked.Add(request);
            return Task.FromResult(answer);
        }
    }

    private sealed record Fixture(
        UsersViewModel Vm,
        UserStore Users,
        PermissionsStore Permissions,
        RouterStore Router,
        SettingsStore Settings);

    private Fixture Build(params (string Variable, string Value)[] environment) =>
        Build(new StubConfirmation(answer: true), environment);

    private Fixture Build(StubConfirmation confirm, params (string Variable, string Value)[] environment)
    {
        var map = environment.ToDictionary(e => e.Variable, e => e.Value, StringComparer.Ordinal);
        var paths = new FakeAppPaths(_dir.Path);
        var users = new UserStore(paths);
        var permissions = new PermissionsStore(paths);
        var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var administration = new AdministrationPolicy(settings, name => map.GetValueOrDefault(name));

        var router = new RouterStore(paths, users);
        return new Fixture(
            new UsersViewModel(users, permissions, [permissions, router], settings, administration, confirm),
            users,
            permissions,
            router,
            settings);
    }

    // ---- adding and editing --------------------------------------------------------------------

    [Fact]
    public void The_editor_starts_closed_and_cancel_creates_nothing()
    {
        var f = Build();
        Assert.False(f.Vm.IsEditorOpen);

        f.Vm.AddUserCommand.Execute(null);
        Assert.Equal("Add user", f.Vm.EditorTitle);
        f.Vm.UserName = "Unsaved";
        f.Vm.CancelCommand.Execute(null);

        Assert.False(f.Vm.IsEditorOpen);
        Assert.Empty(f.Users.Snapshot.Users);
    }

    /// <summary>
    /// The key is shown once and nothing re-derives it: only its hash is stored, so a page that could
    /// show it again would be storing it somewhere it should not.
    /// </summary>
    [Fact]
    public void Adding_a_user_shows_its_key_once_and_lists_it()
    {
        var f = Build();

        f.Vm.AddUserCommand.Execute(null);
        f.Vm.UserName = "Banter";
        f.Vm.SaveCommand.Execute(null);

        Assert.True(f.Vm.HasGeneratedKey);
        Assert.NotEmpty(f.Vm.GeneratedKeyNotice);
        Assert.Equal("Banter", f.Users.Resolve(f.Vm.GeneratedKey)!.Name);

        var row = Assert.Single(f.Vm.Users);
        Assert.Equal("Banter", row.Name);
        Assert.Equal("Enabled", row.State);
        Assert.Equal(8, row.KeyFingerprint.Length);

        f.Vm.DismissKeyCommand.Execute(null);
        Assert.False(f.Vm.HasGeneratedKey);
        Assert.Empty(f.Vm.GeneratedKey);
    }

    [Fact]
    public void Selecting_a_user_loads_it_for_editing_and_saving_updates_rather_than_adding()
    {
        var f = Build();
        f.Users.Create("agent");
        var reloaded = Build();

        reloaded.Vm.SelectedUser = reloaded.Vm.Users.Single();
        Assert.True(reloaded.Vm.IsEditorOpen);
        Assert.Equal("Edit user: agent", reloaded.Vm.EditorTitle);
        Assert.Equal("agent", reloaded.Vm.UserName);

        reloaded.Vm.UserName = "renamed";
        reloaded.Vm.SaveCommand.Execute(null);

        var user = Assert.Single(reloaded.Users.Snapshot.Users);
        Assert.Equal("renamed", user.Name);

        // Editing an existing user issues no key: the one it holds keeps working.
        Assert.False(reloaded.Vm.HasGeneratedKey);
    }

    /// <summary>
    /// One switch stops everything, and the grants stay exactly as they were — so a user can be put
    /// back without reconstructing what it held.
    /// </summary>
    [Fact]
    public void Suspending_a_user_stops_its_key_and_keeps_its_grants()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Permissions.SetGrants(issued.User.Id, ["kodi__*"]);
        var reloaded = Build();

        reloaded.Vm.SelectedUser = reloaded.Vm.Users.Single();
        reloaded.Vm.UserEnabled = false;
        reloaded.Vm.SaveCommand.Execute(null);

        Assert.Null(reloaded.Users.Resolve(issued.Key));
        Assert.Equal("Suspended", reloaded.Vm.Users.Single().State);
        Assert.Equal(["kodi__*"], reloaded.Permissions.GrantsFor(issued.User.Id)!.Tools);
    }

    [Fact]
    public void Rotating_a_key_shows_the_new_one_and_retires_the_old()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Users.Single();

        reloaded.Vm.RotateKeyCommand.Execute(null);

        Assert.True(reloaded.Vm.HasGeneratedKey);
        Assert.Null(reloaded.Users.Resolve(issued.Key));
        Assert.Equal("agent", reloaded.Users.Resolve(reloaded.Vm.GeneratedKey)!.Name);
    }

    /// <summary>
    /// Removing a user drops its grants with it, so nothing is left granting tools to an id nobody
    /// holds — and a user re-created with the same name starts with nothing rather than inheriting.
    /// </summary>
    [Fact]
    public void Removing_a_user_retires_its_key_and_drops_its_grants()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Permissions.SetGrants(issued.User.Id, ["*"]);
        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Users.Single();

        reloaded.Vm.RemoveCommand.Execute(null);

        Assert.Empty(reloaded.Vm.Users);
        Assert.True(reloaded.Vm.HasNoUsers);
        Assert.Null(reloaded.Users.Resolve(issued.Key));
        Assert.Empty(reloaded.Permissions.Snapshot.Grants);
        Assert.False(reloaded.Vm.IsEditorOpen);
    }

    /// <summary>
    /// Removal has to reach everything keyed to the user, not just grants: a Router route naming an id
    /// nobody holds shows as "(user no longer exists)" on that page and would be inherited by whoever
    /// happened to be created with the same id next.
    /// </summary>
    [Fact]
    public void Removing_a_user_drops_its_router_route_as_well_as_its_grants()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Permissions.SetGrants(issued.User.Id, ["*"]);
        f.Router.SetRoute(issued.User.Id, null);
        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Users.Single();

        reloaded.Vm.RemoveCommand.Execute(null);

        Assert.Empty(reloaded.Permissions.Snapshot.Grants);
        Assert.Empty(reloaded.Router.Snapshot.Inputs);
        Assert.Null(reloaded.Router.Resolve(issued.Key));
    }

    // ---- not misleading the operator -----------------------------------------------------------

    /// <summary>
    /// The choice must read back what it wrote. A control that reverts on the next page load is the bug
    /// an operator cannot diagnose, because nothing is wrong with the thing they were looking at.
    /// </summary>
    [Fact]
    public void Turning_on_per_user_permissions_persists_and_reads_back()
    {
        var f = Build();
        Assert.False(f.Vm.KeysEnforced);
        Assert.True(f.Vm.IsOpenAccess);

        f.Vm.KeysEnforced = true;

        Assert.False(f.Vm.IsOpenAccess);
        Assert.False(f.Permissions.Snapshot.AllowUnauthenticated);
        Assert.True(Build().Vm.KeysEnforced);
    }

    /// <summary>The two radio buttons are one choice, so picking either moves the other.</summary>
    [Fact]
    public void Choosing_either_option_unpicks_the_other()
    {
        var f = Build();

        f.Vm.KeysEnforced = true;
        Assert.False(f.Vm.IsOpenAccess);

        f.Vm.IsOpenAccess = true;
        Assert.False(f.Vm.KeysEnforced);
        Assert.True(f.Permissions.Snapshot.AllowUnauthenticated);
    }

    /// <summary>
    /// Nothing changes until it has been agreed to. There is no halfway state — turning permissions on
    /// refuses every program that has no key — so it is asked for rather than applied and explained
    /// afterwards.
    /// </summary>
    [Fact]
    public void Turning_on_per_user_permissions_is_confirmed_first()
    {
        var confirm = new StubConfirmation(answer: true);
        var f = Build(confirm);
        f.Users.Create("Banter");
        var reloaded = Build(confirm);

        reloaded.Vm.KeysEnforced = true;

        Assert.False(reloaded.Permissions.Snapshot.AllowUnauthenticated);
        var asked = Assert.Single(confirm.Asked);
        Assert.Contains("key", asked.Message, StringComparison.OrdinalIgnoreCase);
        // The part that matters: not that it is safer, but that there is no partial mode.
        Assert.Contains("no partial mode", asked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stops working immediately", asked.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>With nobody set up, turning it on locks everything out — and says so rather than
    /// leaving somebody to find out.</summary>
    [Fact]
    public void With_no_users_the_confirmation_says_nothing_will_reach_the_hub()
    {
        var confirm = new StubConfirmation(answer: true);
        var f = Build(confirm);

        f.Vm.KeysEnforced = true;

        Assert.Contains("no users yet", confirm.Last.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declining_the_confirmation_leaves_the_choice_where_it_was()
    {
        var confirm = new StubConfirmation(answer: false);
        var f = Build(confirm);

        f.Vm.KeysEnforced = true;

        Assert.False(f.Vm.KeysEnforced);
        Assert.True(f.Vm.IsOpenAccess);
        Assert.True(f.Permissions.Snapshot.AllowUnauthenticated);
        Assert.True(Build().Vm.IsOpenAccess);
        Assert.Single(confirm.Asked);
    }

    /// <summary>
    /// And the way back is confirmed too: letting everyone in again hands every tool to anything that
    /// can reach the hub, which is at least as worth a sentence as turning it on.
    /// </summary>
    [Fact]
    public void Going_back_to_allowing_everyone_is_confirmed_too()
    {
        var confirm = new StubConfirmation(answer: true);
        var f = Build(confirm);
        f.Vm.KeysEnforced = true;
        confirm.Asked.Clear();

        f.Vm.IsOpenAccess = true;

        Assert.True(f.Permissions.Snapshot.AllowUnauthenticated);
        Assert.Contains("every tool", confirm.Last.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Loading the page is not a decision, so opening it asks nothing.</summary>
    [Fact]
    public void Opening_the_page_confirms_nothing()
    {
        var confirm = new StubConfirmation(answer: true);
        var f = Build(confirm);
        f.Vm.KeysEnforced = true;
        confirm.Asked.Clear();

        _ = Build(confirm).Vm;

        Assert.Empty(confirm.Asked);
    }

    /// <summary>
    /// Users that exist while keys are not required are inert — every caller is the single user and
    /// gets everything. The list shows them looking perfectly configured, so the page has to say it.
    /// </summary>
    [Fact]
    public void Users_that_are_not_being_enforced_are_warned_about()
    {
        var f = Build();
        Assert.Null(f.Vm.Warning);

        f.Vm.AddUserCommand.Execute(null);
        f.Vm.UserName = "agent";
        f.Vm.SaveCommand.Execute(null);

        Assert.True(f.Vm.HasWarning);
        Assert.Contains("every tool", f.Vm.Warning!, StringComparison.OrdinalIgnoreCase);

        f.Vm.KeysEnforced = true;

        Assert.Null(f.Vm.Warning);
        Assert.False(f.Vm.HasWarning);
    }

    /// <summary>
    /// A switch an environment variable is forcing must be shown locked and named. Otherwise the
    /// checkbox refuses to stay where it is put and nothing on screen explains why.
    /// </summary>
    [Fact]
    public void A_switch_pinned_by_the_environment_is_locked_and_names_what_pins_it()
    {
        var f = Build((AdministrationPolicy.EnabledVariable, "true"));

        Assert.True(f.Vm.AdministerThroughProxy);
        Assert.True(f.Vm.IsAdministrationPinned);
        Assert.False(f.Vm.CanEditAdministration);
        Assert.Contains(
            AdministrationPolicy.EnabledVariable, f.Vm.AdministrationPinnedBy!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unpinned_switch_persists_to_settings()
    {
        var f = Build();
        Assert.False(f.Vm.AdministerThroughProxy);

        f.Vm.AdministerThroughProxy = true;

        Assert.True(f.Settings.Current.AdministrationEnabled);
        Assert.True(f.Vm.CanEditAdministration);
    }

    /// <summary>Nobody can be edited while the directory cannot be read, so the page says so rather
    /// than letting somebody add a user into an exception.</summary>
    [Fact]
    public void A_directory_that_cannot_be_read_is_reported_on_the_page()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "users.json"), "{ not json");

        var f = Build();

        Assert.True(f.Vm.HasConfigurationError);
        Assert.NotNull(f.Vm.ConfigurationError);

        f.Vm.AddUserCommand.Execute(null);
        f.Vm.UserName = "agent";
        f.Vm.SaveCommand.Execute(null);

        Assert.Empty(f.Vm.Users);
        Assert.NotEmpty(f.Vm.StatusMessage);
    }
}
