using MCPHub.App.ViewModels;
using MCPHub.Core.Management;
using MCPHub.Core.Permissions;
using MCPHub.Core.Settings;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// The Permissions page. What is pinned here is mostly about <em>not misleading an operator</em>: a
/// switch that reads back what it just wrote, a warning when the policy is inert, and an explanation
/// that lists every reason a tool is withheld rather than the first.
/// </summary>
public sealed class PermissionsViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed record Fixture(
        PermissionsViewModel Vm, PermissionsStore Store, SettingsStore Settings);

    private Fixture Build(params (string Variable, string Value)[] environment)
    {
        var map = environment.ToDictionary(e => e.Variable, e => e.Value, StringComparer.Ordinal);
        string? Read(string name) => map.GetValueOrDefault(name);

        var paths = new FakeAppPaths(_dir.Path);
        var store = new PermissionsStore(paths);
        var settings = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var management = new PermissionsManagementPolicy(settings, Read);
        var authorization = new CompositeToolAuthorization(
            new PermissionsToolAuthorization(store),
            management,
            new AgentManagementPolicy(settings, Read));

        return new Fixture(new PermissionsViewModel(store, settings, management, authorization), store, settings);
    }

    // ---- principals -----------------------------------------------------------------------------

    [Fact]
    public void The_editor_starts_closed_and_cancel_creates_nothing()
    {
        var f = Build();
        Assert.False(f.Vm.IsEditorOpen);

        f.Vm.AddPrincipalCommand.Execute(null);
        Assert.Equal("Add principal", f.Vm.EditorTitle);
        f.Vm.PrincipalName = "Unsaved";
        f.Vm.CancelCommand.Execute(null);

        Assert.False(f.Vm.IsEditorOpen);
        Assert.Empty(f.Store.Snapshot.Principals);
    }

    [Fact]
    public void Adding_a_principal_shows_its_key_once_and_lists_it()
    {
        var f = Build();

        f.Vm.AddPrincipalCommand.Execute(null);
        f.Vm.PrincipalName = "Banter";
        f.Vm.PrincipalGrants = "kodi__*\nredis__get";
        f.Vm.SaveCommand.Execute(null);

        Assert.True(f.Vm.HasGeneratedKey);
        Assert.Equal("Banter", f.Store.Resolve(f.Vm.GeneratedKey)!.Name);
        Assert.Equal(["kodi__*", "redis__get"], f.Store.Resolve(f.Vm.GeneratedKey)!.Tools);

        var row = Assert.Single(f.Vm.Principals);
        Assert.Equal("Banter", row.Name);
        Assert.Equal("Enabled", row.State);

        // Dismissing is the only thing that removes it from the screen; nothing re-derives it.
        f.Vm.DismissKeyCommand.Execute(null);
        Assert.False(f.Vm.HasGeneratedKey);
        Assert.Empty(f.Vm.GeneratedKey);
    }

    [Fact]
    public void Selecting_a_principal_loads_it_for_editing_and_saving_updates_rather_than_adding()
    {
        var f = Build();
        f.Store.CreatePrincipal("agent", ["kodi__*"]);
        var reloaded = Build();

        reloaded.Vm.SelectedPrincipal = reloaded.Vm.Principals.Single();
        Assert.True(reloaded.Vm.IsEditorOpen);
        Assert.Equal("Edit principal: agent", reloaded.Vm.EditorTitle);
        Assert.Equal("kodi__*", reloaded.Vm.PrincipalGrants);

        reloaded.Vm.PrincipalGrants = "redis__get";
        reloaded.Vm.SaveCommand.Execute(null);

        Assert.Single(reloaded.Store.Snapshot.Principals);
        Assert.Equal(["redis__get"], reloaded.Store.Snapshot.Principals[0].Tools);
    }

    [Fact]
    public void Rotating_replaces_the_key_and_removing_retires_it()
    {
        var f = Build();
        f.Vm.AddPrincipalCommand.Execute(null);
        f.Vm.PrincipalName = "agent";
        f.Vm.SaveCommand.Execute(null);
        var first = f.Vm.GeneratedKey;

        f.Vm.SelectedPrincipal = f.Vm.Principals.Single();
        f.Vm.RotateKeyCommand.Execute(null);
        var second = f.Vm.GeneratedKey;

        Assert.NotEqual(first, second);
        Assert.Null(f.Store.Resolve(first));
        Assert.NotNull(f.Store.Resolve(second));

        f.Vm.SelectedPrincipal = f.Vm.Principals.Single();
        f.Vm.RemoveCommand.Execute(null);

        Assert.Empty(f.Vm.Principals);
        Assert.Null(f.Store.Resolve(second));
        Assert.False(f.Vm.HasGeneratedKey);
    }

    [Fact]
    public void A_grant_that_could_never_match_is_reported_rather_than_saved()
    {
        var f = Build();
        f.Vm.AddPrincipalCommand.Execute(null);
        f.Vm.PrincipalName = "agent";
        f.Vm.PrincipalGrants = "two words";

        f.Vm.SaveCommand.Execute(null);

        Assert.Empty(f.Store.Snapshot.Principals);
        Assert.NotEmpty(f.Vm.StatusMessage);
    }

    // ---- not misleading the operator ------------------------------------------------------------

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

        Assert.False(f.Store.Snapshot.AllowUnauthenticated);
        Assert.True(Build().Vm.KeysEnforced);
    }

    /// <summary>
    /// Principals that exist while keys are not required are inert — every caller is the single user
    /// and gets everything. The list shows them looking perfectly configured, so the page has to say
    /// it outright.
    /// </summary>
    [Fact]
    public void A_policy_that_is_not_being_applied_is_warned_about()
    {
        var f = Build();
        Assert.Null(f.Vm.Warning);

        f.Vm.AddPrincipalCommand.Execute(null);
        f.Vm.PrincipalName = "agent";
        f.Vm.PrincipalGrants = "kodi__*";
        f.Vm.SaveCommand.Execute(null);

        Assert.True(f.Vm.HasWarning);
        Assert.Contains("not being enforced", f.Vm.Warning!, StringComparison.OrdinalIgnoreCase);

        f.Vm.KeysEnforced = true;

        Assert.Null(f.Vm.Warning);
    }

    /// <summary>
    /// A switch an environment variable is forcing must be shown locked and named. Otherwise the
    /// checkbox refuses to stay where it is put and nothing on screen explains why.
    /// </summary>
    [Fact]
    public void A_switch_pinned_by_the_environment_is_locked_and_names_what_pins_it()
    {
        var f = Build((PermissionsManagementPolicy.EnabledVariable, "true"));

        Assert.True(f.Vm.ManageThroughProxy);
        Assert.True(f.Vm.IsManageThroughProxyPinned);
        Assert.False(f.Vm.CanEditManageThroughProxy);
        Assert.Contains(
            PermissionsManagementPolicy.EnabledVariable,
            f.Vm.ManageThroughProxyPinnedBy!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_unpinned_switch_persists_to_settings()
    {
        var f = Build();
        Assert.False(f.Vm.ManageThroughProxy);

        f.Vm.ManageThroughProxy = true;

        Assert.True(f.Settings.Current.PermissionsManagementEnabled);
        Assert.True(f.Vm.CanEditManageThroughProxy);
    }

    // ---- explaining -----------------------------------------------------------------------------

    [Fact]
    public void Explain_needs_a_principal_and_a_tool_before_it_says_anything()
    {
        var f = Build();

        f.Vm.ExplainCommand.Execute(null);
        Assert.Contains("principal", f.Vm.ExplainSummary, StringComparison.OrdinalIgnoreCase);

        f.Store.CreatePrincipal("agent", ["*"]);
        var reloaded = Build();
        reloaded.Vm.SelectedPrincipal = reloaded.Vm.Principals.Single();
        reloaded.Vm.ExplainCommand.Execute(null);
        Assert.Contains("tool", reloaded.Vm.ExplainSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_says_plainly_when_a_tool_is_available()
    {
        var f = Build();
        f.Store.CreatePrincipal("agent", ["kodi__*"]);
        var reloaded = Build();
        reloaded.Vm.KeysEnforced = true;
        reloaded.Vm.SelectedPrincipal = reloaded.Vm.Principals.Single();
        reloaded.Vm.ExplainTool = "kodi__play_pause";

        reloaded.Vm.ExplainCommand.Execute(null);

        Assert.Empty(reloaded.Vm.Denials);
        Assert.False(reloaded.Vm.HasDenials);
        Assert.Contains("can use", reloaded.Vm.ExplainSummary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page's reason for existing: a tool withheld by two independent things at once, both named,
    /// with the environment variable that is pinning one of them.
    /// </summary>
    [Fact]
    public void Explain_lists_every_reason_a_tool_is_withheld()
    {
        var f = Build(
            (AgentManagementPolicy.EnabledVariable, "true"),
            (AgentManagementPolicy.InstallVariable, "false"));
        f.Store.CreatePrincipal("agent", ["kodi__*"]);
        f.Store.SetAllowUnauthenticated(false);

        var reloaded = Build(
            (AgentManagementPolicy.EnabledVariable, "true"),
            (AgentManagementPolicy.InstallVariable, "false"));
        reloaded.Vm.SelectedPrincipal = reloaded.Vm.Principals.Single();
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
}
