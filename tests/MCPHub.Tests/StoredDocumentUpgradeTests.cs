using MCPHub.Core.Permissions;
using MCPHub.Core.Routing;
using MCPHub.Core.Users;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// Opening documents written by an older MCPHub.
///
/// <para>These exist because of a launch failure, and the cause is worth stating: the
/// source-generated JSON readers do <b>not</b> run property initialisers, so a member absent from a
/// stored file arrives as <see langword="null"/> or zero rather than as its declared default. Every
/// property added after a file was first written is therefore absent in somebody's settings folder,
/// and a reader that assumes otherwise crashes on the way to the first page — which is what happened
/// to <c>permissions.json</c> when grants replaced principals.</para>
///
/// <para>So each store is tested against a document that predates its newest members, and the rule is
/// the same everywhere: missing means "none", never a crash and never a failure to start.</para>
/// </summary>
public sealed class StoredDocumentUpgradeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private FakeAppPaths Paths => new(_dir.Path);

    private void Write(string name, string json) =>
        File.WriteAllText(Path.Combine(_dir.Path, name), json);

    /// <summary>
    /// The exact file that would not launch: written when permissions carried principals, so it has no
    /// grants section at all.
    /// </summary>
    [Fact]
    public void A_permissions_document_from_before_grants_existed_opens_and_grants_nothing()
    {
        Write("permissions.json", """{"SchemaVersion":1,"AllowUnauthenticated":true,"Principals":[]}""");

        var store = new PermissionsStore(Paths, new UserStore(Paths));

        Assert.Null(store.LoadError);
        Assert.Empty(store.Snapshot.Grants);
        Assert.True(store.Snapshot.AllowUnauthenticated);

        // And it is still editable, which is what "the hub launched" has to mean in practice.
        store.SetGrants("someone", ["kodi__*"]);
        Assert.Equal(["kodi__*"], new PermissionsStore(Paths).GrantsFor("someone")!.Tools);
    }

    /// <summary>
    /// A principal carried a name, a key hash and its tools. All three come across: the key an agent
    /// holds keeps working, and what it could use is still granted to it.
    /// </summary>
    [Fact]
    public void Principals_become_users_keeping_their_keys_and_their_grants()
    {
        var hash = UserKeys.Hash("a-key-the-old-permissions-page-issued");
        Write("permissions.json", $$"""
            {
              "SchemaVersion": 1,
              "AllowUnauthenticated": false,
              "Principals": [
                { "Id": "banter", "Name": "Banter", "KeyHash": "{{hash}}", "Enabled": true, "Tools": ["*"] }
              ]
            }
            """);
        var users = new UserStore(Paths);

        var store = new PermissionsStore(Paths, users);

        Assert.Null(store.LoadError);
        Assert.Equal("banter", users.Resolve("a-key-the-old-permissions-page-issued")!.Id);
        Assert.Equal("Banter", users.Snapshot.Users[0].Name);
        Assert.Equal(["*"], store.GrantsFor("banter")!.Tools);
        Assert.False(store.Snapshot.AllowUnauthenticated);
    }

    [Fact]
    public void A_suspended_principal_arrives_suspended()
    {
        var hash = UserKeys.Hash("another-key-the-old-permissions-page-issued");
        Write("permissions.json", $$"""
            {
              "SchemaVersion": 1,
              "Principals": [
                { "Id": "paused", "Name": "Paused", "KeyHash": "{{hash}}", "Enabled": false, "Tools": [] }
              ]
            }
            """);
        var users = new UserStore(Paths);

        var store = new PermissionsStore(Paths, users);

        Assert.False(Assert.Single(users.Snapshot.Users).Enabled);
        Assert.Null(users.Resolve("another-key-the-old-permissions-page-issued"));
        Assert.Empty(store.GrantsFor("paused")!.Tools);
    }

    /// <summary>Without somewhere to migrate to, the hub still comes up — granting nothing, which is
    /// the only safe direction for this to fail.</summary>
    [Fact]
    public void A_legacy_document_with_no_directory_to_migrate_into_still_opens()
    {
        Write("permissions.json", """{"SchemaVersion":1,"Principals":[{"Id":"x","KeyHash":"ab"}]}""");

        var store = new PermissionsStore(Paths);

        Assert.Null(store.LoadError);
        Assert.Empty(store.Snapshot.Grants);
    }

    [Fact]
    public void A_users_document_with_no_users_member_opens_and_recognises_nobody()
    {
        Write("users.json", """{"SchemaVersion":1}""");

        var store = new UserStore(Paths);

        Assert.Null(store.LoadError);
        Assert.Empty(store.Snapshot.Users);
        Assert.Null(store.Resolve("anything-at-all-whatsoever-0123456789"));

        // Editable, so the Users page is not dead on arrival either.
        Assert.NotEmpty(store.Create("agent").Key);
    }

    /// <summary>
    /// A router document from before a member existed must not read as "port zero" — the symptom that
    /// first exposed this, since zero fails validation and the whole routing table reads as corrupt.
    /// </summary>
    [Fact]
    public void A_router_document_missing_its_newer_members_opens_on_the_default_listener()
    {
        Write("router.json", """{"Outputs":[],"Inputs":[]}""");

        var store = new RouterStore(Paths, new UserStore(Paths));

        Assert.Null(store.LoadError);
        Assert.Equal(RouterConfigurationRules.DefaultPort, store.Snapshot.Port);
        Assert.Equal(RouterConfigurationRules.Loopback, store.Snapshot.BindAddress);
        Assert.Empty(store.Snapshot.Inputs);
    }

    [Fact]
    public void A_router_document_with_no_lists_at_all_opens_rather_than_reading_as_corrupt()
    {
        Write("router.json", """{"SchemaVersion":1,"Port":5801}""");

        var store = new RouterStore(Paths, new UserStore(Paths));

        Assert.Null(store.LoadError);
        Assert.Empty(store.Snapshot.Inputs);
        Assert.Empty(store.Snapshot.Outputs);
    }
}
