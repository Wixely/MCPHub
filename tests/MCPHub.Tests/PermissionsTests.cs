using System.Text.Json;
using MCPHub.Core.Permissions;
using MCPHub.Core.Users;
using MCPHub.Proxy;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// The hub as the authority on which tools a user may use: grant matching, and the read-only
/// deployment document a container mounts. Identity — keys, hashing, suspension — is
/// <see cref="UserDirectoryTests"/>'s business, and the split is the point: these tests must never
/// need a key to decide what a user may do.
/// </summary>
public sealed class PermissionsTests
{
    private static PermissionsGrant Grant(string userId, params string[] tools) =>
        new() { UserId = userId, Tools = tools };

    private static IUserDirectory Directory(params string[] ids) => new StaticUserDirectory(
        new HubUsersConfiguration
        {
            Users = [.. ids.Select(id => new HubUser { Id = id, Name = id, KeyHash = UserKeys.Hash($"key-{id}") })],
        });

    // ---- grant matching ------------------------------------------------------------------------

    [Fact]
    public void Exact_grant_matches_only_that_tool()
    {
        var grant = Grant("alice", "kodi__play_pause");

        Assert.True(PermissionsConfigurationRules.Covers(grant, "kodi", "kodi__play_pause"));
        Assert.False(PermissionsConfigurationRules.Covers(grant, "kodi", "kodi__stop"));
    }

    [Fact]
    public void Server_wildcard_covers_that_server_and_no_other()
    {
        var grant = Grant("alice", "kodi__*");

        Assert.True(PermissionsConfigurationRules.Covers(grant, "kodi", "kodi__play_pause"));
        Assert.True(PermissionsConfigurationRules.Covers(grant, "kodi", "kodi__anything_added_later"));
        Assert.False(PermissionsConfigurationRules.Covers(grant, "redis", "redis__get"));
    }

    /// <summary>
    /// A server key containing the namespace separator must not be matched by halves — otherwise
    /// "a__*" would quietly grant every tool of a server called "a__b".
    /// </summary>
    [Fact]
    public void Server_wildcard_is_compared_against_the_key_not_the_tool_prefix()
    {
        var grant = Grant("alice", "a__*");

        Assert.False(PermissionsConfigurationRules.Covers(grant, "a__b", "a__b__get"));
        Assert.True(PermissionsConfigurationRules.Covers(grant, "a", "a__get"));
    }

    /// <summary>
    /// The widest grant exists for installation: a caller that may bring up servers has to be able to
    /// use tools that do not exist when the grant is written.
    /// </summary>
    [Fact]
    public void Everything_grant_covers_servers_that_do_not_exist_yet()
    {
        var grant = Grant("banter", "*");

        Assert.True(PermissionsConfigurationRules.Covers(grant, "mcphub", "mcphub__install"));
        Assert.True(PermissionsConfigurationRules.Covers(grant, "not-installed-yet", "whatever__tool"));
    }

    [Fact]
    public void No_grants_at_all_covers_nothing()
    {
        Assert.False(PermissionsConfigurationRules.Covers(Grant("alice"), "kodi", "kodi__play_pause"));
    }

    // ---- validation ----------------------------------------------------------------------------

    [Fact]
    public void One_user_cannot_hold_two_sets_of_grants()
    {
        var configuration = new PermissionsConfiguration
        {
            Grants = [Grant("alice", "kodi__*"), Grant("alice", "redis__*")],
        };

        Assert.Throws<ArgumentException>(() => PermissionsConfigurationRules.Validate(configuration));
    }

    [Fact]
    public void A_grant_that_names_no_user_is_refused()
    {
        var configuration = new PermissionsConfiguration { Grants = [Grant("  ", "*")] };

        Assert.Throws<ArgumentException>(() => PermissionsConfigurationRules.Validate(configuration));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    public void A_grant_that_matches_nothing_is_refused_rather_than_looking_like_a_grant(string pattern)
    {
        var configuration = new PermissionsConfiguration { Grants = [Grant("alice", pattern)] };

        Assert.Throws<ArgumentException>(() => PermissionsConfigurationRules.Validate(configuration));
    }

    // ---- enforcement at the proxy --------------------------------------------------------------

    /// <summary>
    /// The single-user hub must keep working unchanged: no keys issued, no users, everything visible.
    /// That is what AllowUnauthenticated defaults true for.
    /// </summary>
    [Fact]
    public void With_no_users_the_default_tenant_still_sees_everything()
    {
        var policy = new PermissionsToolAuthorization(
            Directory(), new StaticPermissionsSource(new PermissionsConfiguration()));

        Assert.True(policy.IsToolVisible(TenantContext.Default, "kodi", "kodi__play_pause"));
        Assert.True(policy.IsCallAllowed(TenantContext.Default, "kodi", "kodi__play_pause"));
    }

    /// <summary>
    /// And a deployment that issues keys turns that off, after which an unauthenticated caller gets
    /// nothing rather than everything — the only safe direction for that switch to fail.
    /// </summary>
    [Fact]
    public void Once_keys_are_issued_an_unauthenticated_caller_gets_nothing()
    {
        var policy = new PermissionsToolAuthorization(
            Directory("alice"),
            new StaticPermissionsSource(new PermissionsConfiguration
            {
                AllowUnauthenticated = false,
                Grants = [Grant("alice", "*")],
            }));

        Assert.False(policy.IsToolVisible(TenantContext.Default, "kodi", "kodi__play_pause"));
        Assert.True(policy.IsToolVisible(new TenantContext("alice"), "kodi", "kodi__play_pause"));
    }

    [Fact]
    public void Two_users_see_disjoint_catalogues_from_one_policy()
    {
        var policy = new PermissionsToolAuthorization(
            Directory("alice", "bob"),
            new StaticPermissionsSource(new PermissionsConfiguration
            {
                AllowUnauthenticated = false,
                Grants = [Grant("alice", "kodi__*"), Grant("bob", "redis__get")],
            }));

        Assert.True(policy.IsToolVisible(new TenantContext("alice"), "kodi", "kodi__play_pause"));
        Assert.False(policy.IsToolVisible(new TenantContext("alice"), "redis", "redis__get"));
        Assert.True(policy.IsToolVisible(new TenantContext("bob"), "redis", "redis__get"));
        Assert.False(policy.IsToolVisible(new TenantContext("bob"), "kodi", "kodi__play_pause"));
    }

    /// <summary>
    /// A user with a key but no grants may use nothing. Absence is the default precisely so that
    /// issuing a key is not the same act as handing over the hub.
    /// </summary>
    [Fact]
    public void A_user_with_no_grants_may_use_nothing()
    {
        var policy = new PermissionsToolAuthorization(
            Directory("alice"),
            new StaticPermissionsSource(new PermissionsConfiguration { AllowUnauthenticated = false }));

        Assert.False(policy.IsToolVisible(new TenantContext("alice"), "kodi", "kodi__play_pause"));
    }

    /// <summary>
    /// Suspension is the one switch that stops everything: the grants stay exactly as they were, so
    /// the user can be restored without reconstructing what it held.
    /// </summary>
    [Fact]
    public void A_suspended_user_is_granted_nothing_it_was_granted_before()
    {
        var users = new StaticUserDirectory(new HubUsersConfiguration
        {
            Users = [new HubUser { Id = "alice", Name = "Alice", Enabled = false, KeyHash = UserKeys.Hash("key") }],
        });
        var source = new StaticPermissionsSource(new PermissionsConfiguration { Grants = [Grant("alice", "*")] });
        var policy = new PermissionsToolAuthorization(users, source);

        Assert.False(policy.IsToolVisible(new TenantContext("alice"), "kodi", "kodi__play_pause"));
        Assert.Equal(["*"], source.GrantsFor("alice")!.Tools);
    }

    /// <summary>
    /// A tenant whose user has gone must stop working at once rather than when it next reconnects —
    /// the proxy asks the policy on every call, so deleting a user is immediate.
    /// </summary>
    [Fact]
    public void A_tenant_with_no_user_is_refused_even_when_unauthenticated_callers_are_allowed()
    {
        var policy = new PermissionsToolAuthorization(
            Directory("alice"),
            new StaticPermissionsSource(new PermissionsConfiguration
            {
                AllowUnauthenticated = true,
                Grants = [Grant("alice", "*")],
            }));

        Assert.False(policy.IsToolVisible(new TenantContext("deleted"), "kodi", "kodi__play_pause"));
    }

    /// <summary>
    /// Visibility and callability must never disagree: advertising a tool then refusing it reads as a
    /// broken server, and hiding one that would run is worse.
    /// </summary>
    [Fact]
    public void Visibility_and_callability_always_agree()
    {
        var policy = new PermissionsToolAuthorization(
            Directory("alice"),
            new StaticPermissionsSource(new PermissionsConfiguration
            {
                AllowUnauthenticated = false,
                Grants = [Grant("alice", "kodi__play_pause")],
            }));

        foreach (var (tenant, key, tool) in new[]
                 {
                     ("alice", "kodi", "kodi__play_pause"),
                     ("alice", "kodi", "kodi__stop"),
                     ("nobody", "kodi", "kodi__play_pause"),
                 })
        {
            var context = new TenantContext(tenant);
            Assert.Equal(policy.IsToolVisible(context, key, tool), policy.IsCallAllowed(context, key, tool));
        }
    }

    // ---- the deployment document ---------------------------------------------------------------

    /// <summary>
    /// A grant document holds no secrets, which is the whole reason it is simpler than the user
    /// directory's: it names users and tools, so it can sit in source control as it is.
    /// </summary>
    [Fact]
    public void A_mounted_document_is_read_as_written_and_contains_no_credentials()
    {
        using var f = new Fixture();

        var source = new PermissionsDeploymentSource(f.FilePath);

        Assert.Equal(["*"], source.GrantsFor("banter")!.Tools);
        Assert.Null(source.GrantsFor("nobody"));
        Assert.False(source.Snapshot.AllowUnauthenticated);
        Assert.Null(source.ReloadError);
        Assert.DoesNotContain("KeyHash", File.ReadAllText(f.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A host with no valid policy must not start. The message carries no payload: this document is
    /// the statement of who may do what, and an exception is not where it should be quoted.
    /// </summary>
    [Fact]
    public void An_unreadable_document_stops_the_host_without_quoting_it()
    {
        using var f = new Fixture();
        File.WriteAllText(f.FilePath, "{ not json");

        var thrown = Assert.Throws<InvalidOperationException>(() => new PermissionsDeploymentSource(f.FilePath));

        Assert.DoesNotContain("not json", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(f.FilePath, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A running host keeps the policy it had when a new file cannot be enforced. For an allow-list
    /// the alternative is worse than stale: half a document in force grants more than it says.
    /// </summary>
    [Fact]
    public void A_broken_reload_keeps_the_policy_already_in_force()
    {
        using var f = new Fixture();
        var source = new PermissionsDeploymentSource(f.FilePath);

        File.WriteAllText(f.FilePath, "{ broken");

        Assert.False(source.Reload());
        Assert.NotNull(source.ReloadError);
        Assert.Equal(["*"], source.GrantsFor("banter")!.Tools);
    }

    [Fact]
    public void A_good_reload_replaces_the_policy_in_force()
    {
        using var f = new Fixture();
        var source = new PermissionsDeploymentSource(f.FilePath);
        Assert.True(source.Reload());

        f.Document = f.Document with { Grants = [Grant("banter", "kodi__play_pause")] };
        f.Save();

        Assert.True(source.Reload());
        Assert.Null(source.ReloadError);
        Assert.Equal(["kodi__play_pause"], source.GrantsFor("banter")!.Tools);
    }

    /// <summary>
    /// A document that would be enforced differently from how it reads is refused on the first load
    /// too, not only on reload — otherwise a container would come up believing a policy nobody wrote.
    /// </summary>
    [Fact]
    public void A_document_with_two_entries_for_one_user_is_refused_at_startup()
    {
        using var f = new Fixture();
        f.Document = f.Document with { Grants = [Grant("banter", "*"), Grant("banter", "kodi__*")] };
        f.Save();

        Assert.Throws<InvalidOperationException>(() => new PermissionsDeploymentSource(f.FilePath));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "mcphub-perms-" + Guid.NewGuid().ToString("n")[..8]);
            System.IO.Directory.CreateDirectory(DirectoryPath);
            FilePath = Path.Combine(DirectoryPath, "permissions.json");
            Document = new PermissionsConfiguration
            {
                AllowUnauthenticated = false,
                Grants = [Grant("banter", "*")],
            };
            Save();
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }

        public PermissionsConfiguration Document { get; set; }

        public void Save() => File.WriteAllText(
            FilePath, JsonSerializer.Serialize(Document, new JsonSerializerOptions { WriteIndented = true }));

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
                // A test's temp directory outliving the test is not the test's problem.
            }
        }
    }
}
