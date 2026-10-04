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

        return new Fixture(new PermissionsViewModel(users, store, authorization), users, store);
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
            f.Vm.AddUserCommand.Execute(null);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(subscriber);
        }

        Assert.Equal("Users", Assert.Single(asked));
    }

    // ---- editing grants ------------------------------------------------------------------------

    [Fact]
    public void Selecting_a_user_loads_its_grants_one_per_line()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        f.Store.SetGrants(issued.User.Id, ["kodi__*", "redis__get"]);
        var reloaded = Build();

        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();

        Assert.True(reloaded.Vm.HasUserSelected);
        Assert.Contains("Tools for agent", reloaded.Vm.EditorTitle, StringComparison.Ordinal);
        Assert.Equal($"kodi__*{Environment.NewLine}redis__get", reloaded.Vm.Grants);
    }

    /// <summary>
    /// Saving must not close the editor: an operator adjusting grants on one user does several passes,
    /// and losing the selection after each one is the kind of thing that makes a page tiring to use.
    /// </summary>
    [Fact]
    public void Saving_grants_keeps_the_user_selected()
    {
        var f = Build();
        var issued = f.Users.Create("agent");
        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();

        reloaded.Vm.Grants = "kodi__*\nredis__get";
        reloaded.Vm.SaveCommand.Execute(null);

        Assert.Equal(["kodi__*", "redis__get"], reloaded.Store.GrantsFor(issued.User.Id)!.Tools);
        Assert.NotNull(reloaded.Vm.SelectedUser);
        Assert.Equal(["kodi__*", "redis__get"], reloaded.Vm.SelectedUser!.Tools);
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

    [Fact]
    public void A_grant_that_could_never_match_is_reported_rather_than_saved()
    {
        var f = Build();
        f.Users.Create("agent");
        var reloaded = Build();
        reloaded.Vm.SelectedUser = reloaded.Vm.Rows.Single();

        reloaded.Vm.Grants = "two words";
        reloaded.Vm.SaveCommand.Execute(null);

        Assert.Empty(reloaded.Store.Snapshot.Grants);
        Assert.NotEmpty(reloaded.Vm.StatusMessage);
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
