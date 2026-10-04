using MCPHub.App.ViewModels;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Routing;
using MCPHub.Core.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

public sealed class RouterViewModelTests
{
    [Fact]
    public async Task Editors_start_closed_and_cancel_never_creates_an_item()
    {
        await using var f = new Fixture();
        Assert.False(f.Vm.IsInputEditorOpen);
        Assert.False(f.Vm.IsOutputEditorOpen);
        f.Vm.NewOutputCommand.Execute(null);
        Assert.Equal("Add model output", f.Vm.OutputEditorTitle);
        Assert.Equal("Add output", f.Vm.OutputSaveText);
        f.Vm.OutputName = "Unsaved";
        f.Vm.CancelOutputCommand.Execute(null);
        Assert.False(f.Vm.IsOutputEditorOpen);
        Assert.Empty(f.Store.Snapshot.Outputs);
        f.AddUser();
        f.Vm.NewInputCommand.Execute(null);
        Assert.Equal("Give a user Router access", f.Vm.InputEditorTitle);
        f.Vm.CancelInputCommand.Execute(null);
        Assert.False(f.Vm.IsInputEditorOpen);
        Assert.Empty(f.Store.Snapshot.Inputs);
    }

    [Fact]
    public async Task Saved_output_returns_to_list_and_edit_updates_instead_of_adding()
    {
        await using var f = new Fixture();
        f.AddOutput();
        Assert.False(f.Vm.IsOutputEditorOpen);
        f.Vm.OutputRows.Single().EditCommand.Execute(null);
        Assert.Equal("Edit output: Local model", f.Vm.OutputEditorTitle);
        Assert.Equal("Save changes", f.Vm.OutputSaveText);
        f.Vm.OutputName = "Renamed";
        Assert.Equal("Edit output: Local model", f.Vm.OutputEditorTitle);
        f.Vm.SaveOutputCommand.Execute(null);
        Assert.Equal("Renamed", Assert.Single(f.Store.Snapshot.Outputs).Name);
        Assert.False(f.Vm.IsOutputEditorOpen);
    }

    /// <summary>
    /// Giving access issues nothing: the user arrives with a key from the Users page and that key starts
    /// working here. The page used to mint a second one, which is how an agent ended up holding two.
    /// </summary>
    [Fact]
    public async Task Giving_a_user_access_uses_the_key_it_already_holds()
    {
        await using var f = new Fixture();
        var user = f.AddUser("Coding agent");
        f.Vm.NewInputCommand.Execute(null);

        Assert.Equal("Give a user Router access", f.Vm.InputEditorTitle);
        Assert.Equal("Coding agent", Assert.Single(f.Vm.UserChoices).Name);
        Assert.Null(f.Vm.NoUsersHint);

        f.Vm.SaveInputCommand.Execute(null);

        Assert.False(f.Vm.IsInputEditorOpen);
        Assert.Equal(user.Id, Assert.Single(f.Store.Snapshot.Inputs).UserId);
        Assert.Equal(user.Id, f.Store.Resolve(user.Key)!.UserId);
        Assert.Equal("Coding agent", Assert.Single(f.Vm.InputRows).Name);
        Assert.Equal("Enabled", f.Vm.InputRows.Single().State);
    }

    /// <summary>
    /// And a user already holding a route is still offered while it is the one being edited — otherwise
    /// the dropdown would show somebody else's name against the route on screen.
    /// </summary>
    [Fact]
    public async Task A_routed_user_is_offered_only_while_its_own_route_is_open()
    {
        await using var f = new Fixture();
        f.AddUser("Only agent");
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.SaveInputCommand.Execute(null);

        f.Vm.NewInputCommand.Execute(null);
        Assert.Empty(f.Vm.UserChoices);
        Assert.Contains("already has Router access", f.Vm.NoUsersHint!);

        f.Vm.InputRows.Single().EditCommand.Execute(null);
        Assert.Equal("Only agent", Assert.Single(f.Vm.UserChoices).Name);
        Assert.Equal("Router access for Only agent", f.Vm.InputEditorTitle);
    }

    /// <summary>With nobody to grant, the page says where users come from rather than offering an empty
    /// list and a button that does nothing.</summary>
    [Fact]
    public async Task With_no_users_at_all_the_editor_says_where_they_come_from()
    {
        await using var f = new Fixture();
        f.Vm.NewInputCommand.Execute(null);

        Assert.Empty(f.Vm.UserChoices);
        Assert.True(f.Vm.HasNoUsersHint);
        Assert.Contains("Users page", f.Vm.NoUsersHint!);

        f.Vm.SaveInputCommand.Execute(null);

        Assert.Empty(f.Store.Snapshot.Inputs);
        Assert.Contains("Users page", f.Vm.StatusMessage);
    }

    /// <summary>
    /// Revoking a route leaves the user and its key alone — the distinction that could not exist while
    /// the Router issued its own credentials.
    /// </summary>
    [Fact]
    public async Task Revoking_access_leaves_the_user_and_its_key_in_place()
    {
        await using var f = new Fixture();
        var user = f.AddUser();
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.SaveInputCommand.Execute(null);

        f.Vm.InputRows.Single().EditCommand.Execute(null);
        f.Vm.RemoveInputCommand.Execute(null);

        Assert.Empty(f.Store.Snapshot.Inputs);
        Assert.Null(f.Store.Resolve(user.Key));
        Assert.NotNull(f.Users.Resolve(user.Key));
    }

    /// <summary>
    /// A user suspended on the Users page keeps its route and stops working, and the row says which it
    /// is — a route that looks live and does nothing is the thing worth seeing here.
    /// </summary>
    [Fact]
    public async Task A_suspended_user_keeps_its_route_and_is_shown_as_suspended()
    {
        await using var f = new Fixture();
        var user = f.AddUser();
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.SaveInputCommand.Execute(null);

        f.Users.SetEnabled(user.Id, false);

        // The page's own tick, which is how a change made on the Users page reaches this one.
        f.Vm.RefreshActivity();

        Assert.Equal("Suspended", f.Vm.InputRows.Single().State);
        Assert.Null(f.Store.Resolve(user.Key));
    }

    [Fact]
    public async Task Saving_output_preserves_agent_draft_and_edit_mode()
    {
        await using var f = new Fixture();
        f.AddUser("Draft agent");
        f.Vm.NewInputCommand.Execute(null);
        f.AddOutput();
        Assert.True(f.Vm.IsInputEditorOpen);
        Assert.Equal("Draft agent", f.Vm.InputUser!.Name);
        Assert.Equal("Give a user Router access", f.Vm.InputEditorTitle);
        f.Vm.OutputRows.Single().EditCommand.Execute(null);
        f.Vm.OutputName = "Draft output rename";
        f.Vm.SaveInputCommand.Execute(null);
        Assert.True(f.Vm.IsOutputEditorOpen);
        Assert.Equal("Draft output rename", f.Vm.OutputName);
        Assert.Equal("Edit output: Local model", f.Vm.OutputEditorTitle);
    }

    [Fact]
    public async Task Saved_routes_and_draft_routes_are_distinguished()
    {
        await using var f = new Fixture();
        f.AddOutput();
        f.AddUser();
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.SaveInputCommand.Execute(null);
        Assert.Equal("No destination assigned", f.Vm.InputRows.Single().RouteSummary);
        Assert.StartsWith("No global default", f.Vm.SavedDefaultSummary);
        f.Vm.DefaultOutput = f.Vm.DefaultChoices[1];
        Assert.StartsWith("No global default", f.Vm.SavedDefaultSummary);
        f.Vm.SaveDefaultCommand.Execute(null);
        Assert.Equal("Global default → Local model", f.Vm.InputRows.Single().RouteSummary);
        f.Vm.InputRows.Single().EditCommand.Execute(null);
        f.Vm.InputOutput = f.Vm.InputChoices[1];
        Assert.StartsWith("Destination after saving: Local model", f.Vm.InputRoutePreview);
        Assert.Equal("Global default → Local model", f.Vm.InputRows.Single().RouteSummary);
        f.Vm.SaveInputCommand.Execute(null);
        Assert.Equal("Assigned output → Local model", f.Vm.InputRows.Single().RouteSummary);
    }

    /// <summary>A key rotated on the Users page keeps the route it had: the route names the user, not the
    /// credential, so rotating is no longer something the Router has to know about.</summary>
    [Fact]
    public async Task A_rotated_key_keeps_the_route_it_had()
    {
        await using var f = new Fixture();
        var user = f.AddUser();
        f.AddOutput();
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.InputOutput = f.Vm.InputChoices[1];
        f.Vm.SaveInputCommand.Execute(null);

        var rotated = f.Users.RotateKey(user.Id);

        Assert.Null(f.Store.Resolve(user.Key));
        Assert.Equal("Local model", f.Store.Resolve(rotated)!.Output!.Name);
    }

    [Fact]
    public async Task Validation_failure_keeps_the_editor_and_draft_open()
    {
        await using var f = new Fixture();
        f.Vm.NewOutputCommand.Execute(null);
        f.Vm.OutputName = "Draft output";
        f.Vm.OutputBaseUrl = "not-a-url";
        f.Vm.SaveOutputCommand.Execute(null);
        Assert.True(f.Vm.IsOutputEditorOpen);
        Assert.Equal("Draft output", f.Vm.OutputName);
        Assert.Empty(f.Store.Snapshot.Outputs);
        Assert.Contains("base URL", f.Vm.StatusMessage);
    }

    [Fact]
    public async Task Bind_address_presets_and_apply_persist_without_stopping_the_router_first()
    {
        await using var f = new Fixture();
        Assert.Equal("127.0.0.1", f.Vm.BindAddress);

        f.Vm.UseAllInterfacesCommand.Execute(null);
        Assert.Equal("0.0.0.0", f.Vm.BindAddress);
        Assert.Contains("Every network interface", f.Vm.ListenerSummary);

        f.Vm.Port = 5812;
        await f.Vm.ApplyListenerCommand.ExecuteAsync(null);

        Assert.Equal("0.0.0.0", f.Store.Snapshot.BindAddress);
        Assert.Equal(5812, f.Store.Snapshot.Port);

        f.Vm.UseLoopbackCommand.Execute(null);
        Assert.Contains("This machine only", f.Vm.ListenerSummary);
    }

    [Fact]
    public async Task An_invalid_bind_address_is_reported_and_the_saved_one_is_restored_in_the_box()
    {
        await using var f = new Fixture();
        f.Vm.BindAddress = "my-laptop";
        await f.Vm.ApplyListenerCommand.ExecuteAsync(null);

        Assert.Contains("IP address", f.Vm.StatusMessage);
        Assert.Equal("127.0.0.1", f.Store.Snapshot.BindAddress);
        Assert.Equal("127.0.0.1", f.Vm.BindAddress);
    }

    [Fact]
    public async Task Agent_rows_show_never_connected_until_the_router_sees_their_key()
    {
        await using var f = new Fixture();
        var user = f.AddUser("Coding agent");
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.SaveInputCommand.Execute(null);
        var id = f.Store.Resolve(user.Key)!.UserId;

        Assert.Equal("Never connected", f.Vm.InputRows.Single().ActivitySummary);

        f.Activity.RecordConnection(id);
        f.Vm.RefreshActivity();
        Assert.Contains("Last connected just now", f.Vm.InputRows.Single().ActivitySummary);
        Assert.Contains("1 request", f.Vm.InputRows.Single().ActivitySummary);
    }

    [Fact]
    public async Task Removing_an_agent_forgets_its_activity()
    {
        await using var f = new Fixture();
        var user = f.AddUser("Coding agent");
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.SaveInputCommand.Execute(null);
        var id = f.Store.Resolve(user.Key)!.UserId;
        f.Activity.RecordConnection(id);

        f.Vm.InputRows.Single().EditCommand.Execute(null);
        f.Vm.RemoveInputCommand.Execute(null);

        Assert.False(f.Activity.Get(id).HasConnected);
    }

    [Fact]
    public async Task Testing_an_output_records_its_verdict_on_the_row_and_survives_a_refresh()
    {
        await using var f = new Fixture();
        f.AddOutput();
        f.Tester.Result = new(false, "Nothing is listening on localhost:8000.", TimeSpan.FromMilliseconds(3));

        await f.Vm.OutputRows.Single().TestCommand.ExecuteAsync(null);

        var row = f.Vm.OutputRows.Single();
        Assert.False(row.LastTestPassed);
        Assert.True(row.HasTestResult);
        Assert.Contains("Nothing is listening", row.TestSummary);
        Assert.Contains("Nothing is listening", f.Vm.StatusMessage);

        // A verdict is about the saved output, so an unrelated change must not silently drop it.
        f.Vm.SaveDefaultCommand.Execute(null);
        Assert.Contains("Nothing is listening", f.Vm.OutputRows.Single().TestSummary);
    }

    [Fact]
    public async Task Editing_an_output_clears_the_verdict_that_described_its_old_settings()
    {
        await using var f = new Fixture();
        f.AddOutput();
        await f.Vm.OutputRows.Single().TestCommand.ExecuteAsync(null);
        Assert.True(f.Vm.OutputRows.Single().HasTestResult);

        f.Vm.OutputRows.Single().EditCommand.Execute(null);
        f.Vm.OutputBaseUrl = "http://localhost:9999/v1";
        f.Vm.SaveOutputCommand.Execute(null);

        Assert.False(f.Vm.OutputRows.Single().HasTestResult);
    }

    [Fact]
    public async Task Test_all_reports_how_many_outputs_failed()
    {
        await using var f = new Fixture();
        f.AddOutput();
        f.Vm.NewOutputCommand.Execute(null);
        f.Vm.OutputName = "Second";
        f.Vm.OutputBaseUrl = "http://localhost:8001/v1";
        f.Vm.SaveOutputCommand.Execute(null);

        f.Tester.Result = new(false, "Nothing is listening.", TimeSpan.Zero);
        await f.Vm.TestAllOutputsCommand.ExecuteAsync(null);

        Assert.Equal(2, f.Tester.Tested.Count);
        Assert.Contains("2 of 2 output(s) failed", f.Vm.StatusMessage);
    }

    private sealed class Fixture : IAppPaths, IAsyncDisposable
    {
        public string SettingsDirectory { get; } = Path.Combine(Path.GetTempPath(), "mcphub-router-ui-tests", Guid.NewGuid().ToString("N"));
        public string DataDirectory => SettingsDirectory;
        public string DownloadsDirectory => SettingsDirectory;
        public string DefaultServersDirectory => SettingsDirectory;
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
        public RouterStore Store { get; }
        public UserStore Users { get; }
        public RouterHost Host { get; }
        public RouterActivityLog Activity { get; }
        public StubTester Tester { get; } = new();
        public RouterViewModel Vm { get; }
        public Fixture()
        {
            Directory.CreateDirectory(SettingsDirectory);
            Users = new(this);
            Store = new(this, Users);
            Activity = new(this, writeInterval: TimeSpan.Zero);
            Host = new(Store, NullLogger<RouterHost>.Instance, activity: Activity);
            Vm = new(Store, Users, Host, Activity, Tester);
        }

        /// <summary>A user with a key, as the Users page would make one. The Router page can then give it
        /// access — which is the only way to add a caller now that no key is issued here.</summary>
        public (string Id, string Key) AddUser(string name = "Agent") => Users.Create(name) is var (user, key)
            ? (user.Id, key)
            : default;
        public void AddOutput()
        {
            Vm.NewOutputCommand.Execute(null);
            Vm.OutputName = "Local model";
            Vm.OutputBaseUrl = "http://localhost:8000/v1";
            Vm.SaveOutputCommand.Execute(null);
        }
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Activity.Dispose();
            Directory.Delete(SettingsDirectory, recursive: true);
        }
    }

    /// <summary>Returns a canned verdict so view-model tests never touch the network.</summary>
    internal sealed class StubTester : IRouterOutputTester
    {
        public RouterOutputTestResult Result { get; set; } = new(true, "Reached the provider in 5ms.", TimeSpan.FromMilliseconds(5));
        public List<string> Tested { get; } = [];

        public Task<RouterOutputTestResult> TestAsync(RouterOutput output, CancellationToken cancellationToken = default)
        {
            Tested.Add(output.Id);
            return Task.FromResult(Result);
        }
    }
}
