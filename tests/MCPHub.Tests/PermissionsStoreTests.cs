using MCPHub.Core.Permissions;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// Editing the permissions document: issuing keys, changing grants, and the two failure modes that
/// matter — a key nobody can recover, and a corrupt file that cannot widen access.
/// </summary>
public sealed class PermissionsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private PermissionsStore Store() => new(new FakeAppPaths(_dir.Path));

    private string FilePath => Path.Combine(_dir.Path, "permissions.json");

    // ---- issuing keys --------------------------------------------------------------------------

    [Fact]
    public void A_new_principal_can_use_the_key_it_was_handed()
    {
        var store = Store();

        var (principal, key) = store.CreatePrincipal("Banter", ["*"]);

        Assert.Equal(principal.Id, store.Resolve(key)!.Id);
        Assert.Equal("Banter", store.Resolve(key)!.Name);
    }

    /// <summary>
    /// The key exists once. Only its hash is stored, so a lost key is rotated rather than looked up —
    /// and nothing in the file can be turned back into it.
    /// </summary>
    [Fact]
    public void The_key_is_never_written_down()
    {
        var store = Store();
        var (_, key) = store.CreatePrincipal("Banter", ["*"]);

        Assert.DoesNotContain(key, File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Contains(PermissionsConfigurationRules.HashKey(key), File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Rotating_a_key_issues_a_new_one_and_retires_the_old()
    {
        var store = Store();
        var (principal, first) = store.CreatePrincipal("Banter", ["*"]);

        var second = store.RotateKey(principal.Id);

        Assert.NotEqual(first, second);
        Assert.Null(store.Resolve(first));
        Assert.Equal(principal.Id, store.Resolve(second)!.Id);
    }

    [Fact]
    public void Two_principals_never_share_a_key()
    {
        var store = Store();

        var (_, first) = store.CreatePrincipal("one", []);
        var (_, second) = store.CreatePrincipal("two", []);

        Assert.NotEqual(first, second);
        Assert.NotEqual(store.Resolve(first)!.Id, store.Resolve(second)!.Id);
    }

    // ---- changing policy -----------------------------------------------------------------------

    [Fact]
    public void Grants_can_be_replaced_and_take_effect_at_once()
    {
        var store = Store();
        var (principal, key) = store.CreatePrincipal("Banter", ["kodi__*"]);

        store.SetGrants(principal.Id, ["redis__get", "mcphub__*"]);

        Assert.Equal(["redis__get", "mcphub__*"], store.Resolve(key)!.Tools);
    }

    [Fact]
    public void Disabling_a_principal_stops_its_key_without_losing_its_grants()
    {
        var store = Store();
        var (principal, key) = store.CreatePrincipal("Banter", ["kodi__*"]);

        store.SetEnabled(principal.Id, false);
        Assert.Null(store.Resolve(key));

        store.SetEnabled(principal.Id, true);
        Assert.Equal(["kodi__*"], store.Resolve(key)!.Tools);
    }

    [Fact]
    public void Deleting_a_principal_retires_its_key()
    {
        var store = Store();
        var (principal, key) = store.CreatePrincipal("Banter", ["*"]);

        store.DeletePrincipal(principal.Id);

        Assert.Null(store.Resolve(key));
        Assert.Empty(store.Snapshot.Principals);
    }

    /// <summary>
    /// A management call that reports success having changed nothing is the hardest kind of bug to
    /// see, so an unknown id is refused by name instead.
    /// </summary>
    [Fact]
    public void Editing_a_principal_that_is_not_there_is_refused_by_name()
    {
        var store = Store();

        Assert.Equal("ghost", Assert.Throws<PermissionsNotFoundException>(() => store.SetEnabled("ghost", false)).Id);
        Assert.Throws<PermissionsNotFoundException>(() => store.SetGrants("ghost", []));
        Assert.Throws<PermissionsNotFoundException>(() => store.RotateKey("ghost"));
        Assert.Throws<PermissionsNotFoundException>(() => store.DeletePrincipal("ghost"));
    }

    [Fact]
    public void A_rejected_change_leaves_the_stored_policy_alone()
    {
        var store = Store();
        var (principal, key) = store.CreatePrincipal("Banter", ["kodi__*"]);

        Assert.Throws<ArgumentException>(() => store.SetGrants(principal.Id, ["two words"]));

        Assert.Equal(["kodi__*"], store.Resolve(key)!.Tools);
        Assert.Equal(["kodi__*"], Store().Resolve(key)!.Tools);
    }

    // ---- persistence ---------------------------------------------------------------------------

    [Fact]
    public void Policy_survives_a_restart()
    {
        var (principal, key) = Store().CreatePrincipal("Banter", ["kodi__*", "mcphub__list_services"]);

        var reopened = Store();

        Assert.Equal(principal.Id, reopened.Resolve(key)!.Id);
        Assert.Equal(["kodi__*", "mcphub__list_services"], reopened.Resolve(key)!.Tools);
    }

    [Fact]
    public void The_unauthenticated_switch_persists_too()
    {
        var store = Store();
        Assert.True(store.Snapshot.AllowUnauthenticated);

        store.SetAllowUnauthenticated(false);

        Assert.False(Store().Snapshot.AllowUnauthenticated);
    }

    /// <summary>
    /// A corrupt file must not stop the hub launching, and must not be quietly overwritten either —
    /// somebody may still want to repair it. Failing to an empty document grants nothing, which is
    /// the only direction this is allowed to fail in.
    /// </summary>
    [Fact]
    public void A_corrupt_document_recognises_nobody_and_refuses_to_be_edited()
    {
        var (_, key) = Store().CreatePrincipal("Banter", ["*"]);
        var before = File.ReadAllText(FilePath);
        File.WriteAllText(FilePath, "{ not json");

        var store = Store();

        Assert.NotNull(store.LoadError);
        Assert.Null(store.Resolve(key));
        Assert.Empty(store.Snapshot.Principals);
        Assert.Throws<InvalidOperationException>(() => store.CreatePrincipal("another", ["*"]));
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
        Assert.NotEqual(before, File.ReadAllText(FilePath));
    }

    /// <summary>
    /// A document that parses but could never be enforced is treated the same way — a hash that
    /// cannot match is a principal that is quietly dead, and starting up pretending otherwise would
    /// hide it.
    /// </summary>
    [Fact]
    public void A_document_that_parses_but_cannot_be_enforced_is_also_refused()
    {
        Store().CreatePrincipal("Banter", ["*"]);
        File.WriteAllText(
            FilePath,
            """{ "SchemaVersion": 1, "AllowUnauthenticated": false, "Principals": [ { "Id": "x", "Name": "x", "Enabled": true, "KeyHash": "nope", "Tools": [] } ] }""");

        var store = Store();

        Assert.NotNull(store.LoadError);
        Assert.Empty(store.Snapshot.Principals);
    }

    [Fact]
    public void Nothing_is_written_until_the_first_change()
    {
        _ = Store();

        Assert.False(File.Exists(FilePath), "an untouched hub should not create a permissions file");
    }
}
