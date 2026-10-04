using MCPHub.App.ViewModels;
using MCPHub.Core.Permissions;
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

    private sealed record Fixture(
        UsersViewModel Vm, UserStore Users, PermissionsStore Permissions, SettingsStore Settings);

    private Fixture Build(params (string Variable, string Value)[] environment)
    {
        var map = environment.ToDictionary(e => e.Variable, e => e.Value, StringComparer.Ordinal);
        var paths = new FakeAppPaths(_dir.Path);
        var users = new UserStore(paths);
        var permissions = new PermissionsStore(paths);
        var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var administration = new AdministrationPolicy(settings, name => map.GetValueOrDefault(name));

        return new Fixture(new UsersViewModel(users, permissions, settings, administration), users, permissions, settings);
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

    // ---- not misleading the operator -----------------------------------------------------------

    /// <summary>
    /// The switch must read back what it wrote. A checkbox that reverts on the next page load is the
    /// bug an operator cannot diagnose, because nothing is wrong with the thing they were looking at.
    /// </summary>
    [Fact]
    public void Requiring_a_key_persists_and_reads_back()
    {
        var f = Build();
        Assert.False(f.Vm.KeysEnforced);

        f.Vm.KeysEnforced = true;

        Assert.False(f.Permissions.Snapshot.AllowUnauthenticated);
        Assert.True(Build().Vm.KeysEnforced);
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
