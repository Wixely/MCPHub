using MCPHub.Core.Permissions;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// The desktop's grant document: what persists, and what happens when it cannot be read.
///
/// <para>Grants only. Keys, names and suspension are <see cref="UserDirectoryTests"/>'s, and a test
/// here needing a key would mean the split had failed.</para>
/// </summary>
public sealed class PermissionsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private PermissionsStore Store() => new(new FakeAppPaths(_dir.Path));

    private string FilePath => Path.Combine(_dir.Path, "permissions.json");

    // ---- grants --------------------------------------------------------------------------------

    [Fact]
    public void A_user_with_no_entry_is_granted_nothing()
    {
        var store = Store();

        Assert.Null(store.GrantsFor("alice"));
        Assert.Empty(store.Snapshot.Grants);
    }

    [Fact]
    public void Grants_can_be_replaced_and_take_effect_at_once()
    {
        var store = Store();
        store.SetGrants("alice", ["kodi__*"]);
        Assert.Equal(["kodi__*"], store.GrantsFor("alice")!.Tools);

        store.SetGrants("alice", ["redis__get", "redis__set"]);

        Assert.Equal(["redis__get", "redis__set"], store.GrantsFor("alice")!.Tools);
        Assert.Single(store.Snapshot.Grants);
    }

    /// <summary>
    /// Granting nothing is a state, not a deletion: the user keeps its entry, so an operator can see
    /// it was considered and left with nothing rather than never configured.
    /// </summary>
    [Fact]
    public void Granting_an_empty_list_leaves_the_user_holding_nothing()
    {
        var store = Store();
        store.SetGrants("alice", ["*"]);

        store.SetGrants("alice", []);

        Assert.Empty(store.GrantsFor("alice")!.Tools);
        Assert.False(PermissionsConfigurationRules.Covers(store.GrantsFor("alice")!, "kodi", "kodi__x"));
    }

    [Fact]
    public void Forgetting_a_user_drops_its_grants_and_leaves_the_others()
    {
        var store = Store();
        store.SetGrants("alice", ["kodi__*"]);
        store.SetGrants("bob", ["redis__*"]);

        store.ForgetUser("alice");

        Assert.Null(store.GrantsFor("alice"));
        Assert.Equal(["redis__*"], store.GrantsFor("bob")!.Tools);
    }

    /// <summary>Deleting a user calls this for one that may never have had grants, so it has to be
    /// harmless rather than an error.</summary>
    [Fact]
    public void Forgetting_a_user_that_held_nothing_is_not_an_error()
    {
        var store = Store();
        store.SetGrants("bob", ["redis__*"]);

        store.ForgetUser("never-existed");

        Assert.Single(store.Snapshot.Grants);
    }

    [Fact]
    public void Replacing_every_grant_at_once_is_how_a_migration_arrives()
    {
        var store = Store();
        store.SetGrants("alice", ["kodi__*"]);

        store.Replace([new PermissionsGrant { UserId = "imported", Tools = ["*"] }]);

        Assert.Null(store.GrantsFor("alice"));
        Assert.Equal(["*"], store.GrantsFor("imported")!.Tools);
    }

    // ---- persistence ---------------------------------------------------------------------------

    [Fact]
    public void Grants_survive_a_restart()
    {
        Store().SetGrants("alice", ["kodi__*"]);

        Assert.Equal(["kodi__*"], Store().GrantsFor("alice")!.Tools);
    }

    [Fact]
    public void The_unauthenticated_switch_persists_too()
    {
        Assert.True(Store().Snapshot.AllowUnauthenticated);

        Store().SetAllowUnauthenticated(false);

        Assert.False(Store().Snapshot.AllowUnauthenticated);
    }

    /// <summary>
    /// A hub nobody has configured should leave no trace in the settings directory — the file appearing
    /// is what tells an operator somebody set a policy.
    /// </summary>
    [Fact]
    public void Nothing_is_written_until_the_first_change()
    {
        var store = Store();
        Assert.False(File.Exists(FilePath));

        store.SetAllowUnauthenticated(false);

        Assert.True(File.Exists(FilePath));
    }

    // ---- a document that cannot be read --------------------------------------------------------

    /// <summary>
    /// Corrupt means "grants nothing", not "will not start": the desktop has to come up to be
    /// repaired. An empty document grants nothing, so failing this way cannot widen access.
    /// </summary>
    [Fact]
    public void A_corrupt_document_grants_nothing_and_refuses_to_be_edited()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(FilePath, "{ not json");

        var store = Store();

        Assert.NotNull(store.LoadError);
        Assert.Empty(store.Snapshot.Grants);
        Assert.Throws<InvalidOperationException>(() => store.SetGrants("alice", ["*"]));
        Assert.Throws<InvalidOperationException>(() => store.SetAllowUnauthenticated(false));
        // Still there to be repaired, rather than replaced by an empty one on the way past.
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_document_that_parses_but_cannot_be_enforced_is_also_refused()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(
            FilePath,
            """{"SchemaVersion":1,"Grants":[{"UserId":"alice","Tools":["*"]},{"UserId":"alice","Tools":["kodi__*"]}]}""");

        var store = Store();

        Assert.NotNull(store.LoadError);
        Assert.Empty(store.Snapshot.Grants);
    }

    /// <summary>
    /// Validation happens before the disk is touched, so a change the rules refuse leaves both the
    /// store and the file exactly as they were.
    /// </summary>
    [Fact]
    public void A_rejected_change_leaves_the_stored_policy_alone()
    {
        var store = Store();
        store.SetGrants("alice", ["kodi__*"]);
        var before = File.ReadAllText(FilePath);

        Assert.Throws<ArgumentException>(() => store.SetGrants("alice", ["two words"]));

        Assert.Equal(["kodi__*"], store.GrantsFor("alice")!.Tools);
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_grant_cannot_be_set_for_nobody()
    {
        var store = Store();

        Assert.Throws<ArgumentException>(() => store.SetGrants("  ", ["*"]));
    }
}
