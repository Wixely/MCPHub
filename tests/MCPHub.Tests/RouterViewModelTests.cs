using MCPHub.App.ViewModels;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Routing;
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
        f.Vm.NewInputCommand.Execute(null);
        Assert.Equal("Add agent", f.Vm.InputEditorTitle);
        f.Vm.InputName = "Unsaved";
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

    [Fact]
    public async Task Agent_creation_edit_and_cancel_keep_identity_and_key()
    {
        await using var f = new Fixture();
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.InputName = "Coding agent";
        f.Vm.SaveInputCommand.Execute(null);
        var key = f.Vm.GeneratedKey;
        var id = f.Store.Resolve(key)!.InputId;
        Assert.False(f.Vm.IsInputEditorOpen);
        Assert.Contains("Coding agent", f.Vm.GeneratedKeyNotice);
        f.Vm.InputRows.Single().EditCommand.Execute(null);
        Assert.Equal("Edit agent: Coding agent", f.Vm.InputEditorTitle);
        f.Vm.InputName = "Do not save";
        f.Vm.CancelInputCommand.Execute(null);
        Assert.Equal("Coding agent", Assert.Single(f.Store.Snapshot.Inputs).Name);
        f.Vm.InputRows.Single().EditCommand.Execute(null);
        f.Vm.InputName = "Renamed agent";
        f.Vm.SaveInputCommand.Execute(null);
        Assert.Equal("Renamed agent", Assert.Single(f.Store.Snapshot.Inputs).Name);
        Assert.Equal(id, f.Store.Resolve(key)!.InputId);
        Assert.False(f.Vm.IsInputEditorOpen);
    }

    [Fact]
    public async Task Saving_output_preserves_agent_draft_and_edit_mode()
    {
        await using var f = new Fixture();
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.InputName = "Draft agent";
        f.AddOutput();
        Assert.True(f.Vm.IsInputEditorOpen);
        Assert.Equal("Draft agent", f.Vm.InputName);
        Assert.Equal("Add agent", f.Vm.InputEditorTitle);
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
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.InputName = "Agent";
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

    [Fact]
    public async Task Key_rotation_does_not_save_or_discard_agent_draft()
    {
        await using var f = new Fixture();
        f.Vm.NewInputCommand.Execute(null);
        f.Vm.InputName = "Agent";
        f.Vm.SaveInputCommand.Execute(null);
        var oldKey = f.Vm.GeneratedKey;
        f.Vm.InputRows.Single().EditCommand.Execute(null);
        f.Vm.InputName = "Draft name";
        f.Vm.RotateKeyCommand.Execute(null);
        Assert.Equal("Draft name", f.Vm.InputName);
        Assert.True(f.Vm.IsInputEditorOpen);
        Assert.Equal("Agent", Assert.Single(f.Store.Snapshot.Inputs).Name);
        Assert.Null(f.Store.Resolve(oldKey));
        Assert.NotNull(f.Store.Resolve(f.Vm.GeneratedKey));
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

    private sealed class Fixture : IAppPaths, IAsyncDisposable
    {
        public string SettingsDirectory { get; } = Path.Combine(Path.GetTempPath(), "mcphub-router-ui-tests", Guid.NewGuid().ToString("N"));
        public string DataDirectory => SettingsDirectory;
        public string DownloadsDirectory => SettingsDirectory;
        public string DefaultServersDirectory => SettingsDirectory;
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
        public RouterStore Store { get; }
        public RouterHost Host { get; }
        public RouterViewModel Vm { get; }
        public Fixture()
        {
            Directory.CreateDirectory(SettingsDirectory);
            Store = new(this);
            Host = new(Store, NullLogger<RouterHost>.Instance);
            Vm = new(Store, Host);
        }
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
            Directory.Delete(SettingsDirectory, recursive: true);
        }
    }
}
