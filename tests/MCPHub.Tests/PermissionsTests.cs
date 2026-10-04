using System.Text.Json;
using MCPHub.Core.Permissions;
using MCPHub.Proxy;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// The hub as the authority on who may use which tools: grant matching, key resolution, and the
/// read-only deployment document a container mounts.
/// </summary>
public sealed class PermissionsTests
{
    private static PermissionsPrincipal Principal(string id, params string[] tools) => new()
    {
        Id = id,
        Name = id,
        KeyHash = PermissionsConfigurationRules.HashKey($"key-for-{id}-0123456789abcdef"),
        Tools = tools,
    };

    // ---- grants --------------------------------------------------------------------------------

    [Fact]
    public void Exact_grant_matches_only_that_tool()
    {
        var principal = Principal("alice", "kodi__play_pause");

        Assert.True(PermissionsConfigurationRules.Grants(principal, "kodi", "kodi__play_pause"));
        Assert.False(PermissionsConfigurationRules.Grants(principal, "kodi", "kodi__stop"));
    }

    [Fact]
    public void Server_wildcard_covers_that_server_and_no_other()
    {
        var principal = Principal("alice", "kodi__*");

        Assert.True(PermissionsConfigurationRules.Grants(principal, "kodi", "kodi__play_pause"));
        Assert.True(PermissionsConfigurationRules.Grants(principal, "kodi", "kodi__anything_added_later"));
        Assert.False(PermissionsConfigurationRules.Grants(principal, "redis", "redis__get"));
    }

    /// <summary>
    /// A server key containing the namespace separator must not be matched by halves — otherwise
    /// "a__*" would quietly grant every tool of a server called "a__b".
    /// </summary>
    [Fact]
    public void Server_wildcard_is_compared_against_the_key_not_the_tool_prefix()
    {
        var principal = Principal("alice", "a__*");

        Assert.False(PermissionsConfigurationRules.Grants(principal, "a__b", "a__b__get"));
        Assert.True(PermissionsConfigurationRules.Grants(principal, "a", "a__get"));
    }

    /// <summary>
    /// The widest grant exists for installation: a principal that may bring up servers has to be
    /// able to use tools that do not exist when the grant is written.
    /// </summary>
    [Fact]
    public void Everything_grant_covers_servers_that_do_not_exist_yet()
    {
        var principal = Principal("banter", "*");

        Assert.True(PermissionsConfigurationRules.Grants(principal, "mcphub", "mcphub__install"));
        Assert.True(PermissionsConfigurationRules.Grants(principal, "not-installed-yet", "whatever__tool"));
    }

    [Fact]
    public void A_disabled_principal_is_granted_nothing_it_was_granted_before()
    {
        var principal = Principal("alice", "*") with { Enabled = false };

        Assert.False(PermissionsConfigurationRules.Grants(principal, "kodi", "kodi__play_pause"));
    }

    // ---- keys ----------------------------------------------------------------------------------

    [Fact]
    public void A_key_resolves_to_its_principal_and_an_unknown_key_to_none()
    {
        var source = new StaticPermissionsSource(new PermissionsConfiguration
        {
            Principals = [Principal("alice", "kodi__*"), Principal("bob", "redis__*")],
        });

        Assert.Equal("alice", source.Resolve("key-for-alice-0123456789abcdef")!.Id);
        Assert.Equal("bob", source.Resolve("key-for-bob-0123456789abcdef")!.Id);
        Assert.Null(source.Resolve("not-a-key"));
        Assert.Null(source.Resolve(""));
    }

    /// <summary>
    /// Suspension must be indistinguishable from "unknown" to whoever presents the key: a caller
    /// learning that its key is recognised but switched off is a caller learning it has a valid key.
    /// </summary>
    [Fact]
    public void A_disabled_principals_key_resolves_to_nothing_at_all()
    {
        var source = new StaticPermissionsSource(new PermissionsConfiguration
        {
            Principals = [Principal("alice", "*") with { Enabled = false }],
        });

        Assert.Null(source.Resolve("key-for-alice-0123456789abcdef"));
    }

    [Fact]
    public void Generated_keys_are_unique_long_and_recognisable()
    {
        var keys = Enumerable.Range(0, 64).Select(_ => PermissionsConfigurationRules.NewKey()).ToList();

        Assert.Equal(64, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.StartsWith("mcph_", k, StringComparison.Ordinal));
        Assert.All(keys, k => Assert.True(
            k.Length >= PermissionsConfigurationRules.MinimumKeyLength, $"'{k}' is too short to be a key"));
        // Recognisable in a log and safe in a URL or a shell: nothing that needs escaping.
        Assert.All(keys, k => Assert.DoesNotContain('=', k));
        Assert.All(keys, k => Assert.True(
            k.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), $"'{k}' needs escaping"));
    }

    [Fact]
    public void The_stored_hash_is_not_the_key()
    {
        var key = PermissionsConfigurationRules.NewKey();
        var hash = PermissionsConfigurationRules.HashKey(key);

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(key, hash, StringComparison.Ordinal);
        Assert.Equal(hash, PermissionsConfigurationRules.HashKey(key));
    }

    // ---- validation ----------------------------------------------------------------------------

    [Fact]
    public void Two_principals_cannot_share_a_key()
    {
        var shared = PermissionsConfigurationRules.HashKey("one-key-0123456789abcdefghijklmn");
        var configuration = new PermissionsConfiguration
        {
            Principals =
            [
                Principal("alice") with { KeyHash = shared },
                Principal("bob") with { KeyHash = shared },
            ],
        };

        Assert.Throws<ArgumentException>(() => PermissionsConfigurationRules.Validate(configuration));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("abc")]
    public void A_hash_that_could_never_match_is_refused_rather_than_stored(string hash)
    {
        var configuration = new PermissionsConfiguration { Principals = [Principal("alice") with { KeyHash = hash }] };

        Assert.Throws<ArgumentException>(() => PermissionsConfigurationRules.Validate(configuration));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    public void A_grant_that_matches_nothing_is_refused_rather_than_looking_like_a_grant(string grant)
    {
        var configuration = new PermissionsConfiguration { Principals = [Principal("alice", grant)] };

        Assert.Throws<ArgumentException>(() => PermissionsConfigurationRules.Validate(configuration));
    }

    // ---- enforcement at the proxy --------------------------------------------------------------

    /// <summary>
    /// The single-user hub must keep working unchanged: no keys issued, no tenants, everything
    /// visible. That is what AllowUnauthenticated defaults true for.
    /// </summary>
    [Fact]
    public void With_no_principals_the_default_tenant_still_sees_everything()
    {
        var policy = new PermissionsToolAuthorization(
            new StaticPermissionsSource(new PermissionsConfiguration()));

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
        var policy = new PermissionsToolAuthorization(new StaticPermissionsSource(
            new PermissionsConfiguration { AllowUnauthenticated = false, Principals = [Principal("alice", "*")] }));

        Assert.False(policy.IsToolVisible(TenantContext.Default, "kodi", "kodi__play_pause"));
        Assert.True(policy.IsToolVisible(new TenantContext("alice"), "kodi", "kodi__play_pause"));
    }

    [Fact]
    public void Two_principals_see_disjoint_catalogues_from_one_policy()
    {
        var policy = new PermissionsToolAuthorization(new StaticPermissionsSource(
            new PermissionsConfiguration
            {
                AllowUnauthenticated = false,
                Principals = [Principal("alice", "kodi__*"), Principal("bob", "redis__get")],
            }));

        Assert.True(policy.IsToolVisible(new TenantContext("alice"), "kodi", "kodi__play_pause"));
        Assert.False(policy.IsToolVisible(new TenantContext("alice"), "redis", "redis__get"));
        Assert.True(policy.IsToolVisible(new TenantContext("bob"), "redis", "redis__get"));
        Assert.False(policy.IsToolVisible(new TenantContext("bob"), "kodi", "kodi__play_pause"));
    }

    /// <summary>
    /// A tenant whose principal has gone must stop working at once rather than when it next
    /// reconnects — the proxy asks the policy on every call, so deleting a principal is immediate.
    /// </summary>
    [Fact]
    public void A_tenant_with_no_principal_is_refused_even_when_unauthenticated_callers_are_allowed()
    {
        var policy = new PermissionsToolAuthorization(new StaticPermissionsSource(
            new PermissionsConfiguration { AllowUnauthenticated = true, Principals = [Principal("alice", "*")] }));

        Assert.False(policy.IsToolVisible(new TenantContext("deleted"), "kodi", "kodi__play_pause"));
    }

    /// <summary>
    /// Visibility and callability must never disagree: advertising a tool then refusing it reads as
    /// a broken server, and hiding one that would run is worse.
    /// </summary>
    [Fact]
    public void Visibility_and_callability_always_agree()
    {
        var policy = new PermissionsToolAuthorization(new StaticPermissionsSource(
            new PermissionsConfiguration
            {
                AllowUnauthenticated = false,
                Principals = [Principal("alice", "kodi__play_pause")],
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

    [Fact]
    public void A_mounted_document_resolves_keys_from_a_file_and_the_key_never_appears_in_it()
    {
        using var f = new Fixture();

        var source = new PermissionsDeploymentSource(f.FilePath, _ => null);

        Assert.Equal("banter", source.Resolve(Fixture.Key)!.Id);
        Assert.DoesNotContain(Fixture.Key, File.ReadAllText(f.FilePath), StringComparison.Ordinal);
        Assert.False(source.Snapshot.AllowUnauthenticated);
        Assert.Null(source.ReloadError);
    }

    [Fact]
    public void A_key_can_come_from_the_environment_instead_of_a_file()
    {
        using var f = new Fixture();
        f.Document = f.Document with
        {
            Principals = [f.Document.Principals[0] with { KeyFile = null, KeyEnvironmentVariable = "BANTER_HUB_KEY" }],
        };
        f.Save();

        var source = new PermissionsDeploymentSource(f.FilePath, name => name == "BANTER_HUB_KEY" ? Fixture.Key : null);

        Assert.Equal("banter", source.Resolve(Fixture.Key)!.Id);
    }

    [Fact]
    public void Naming_two_credential_sources_for_one_principal_is_refused()
    {
        using var f = new Fixture();
        f.Document = f.Document with
        {
            Principals =
            [
                f.Document.Principals[0] with { KeyHash = PermissionsConfigurationRules.HashKey(Fixture.Key) },
            ],
        };
        f.Save();

        Assert.Throws<InvalidOperationException>(() => new PermissionsDeploymentSource(f.FilePath, _ => null));
    }

    /// <summary>
    /// A host with no valid policy must not start. The message carries no payload: a key is exactly
    /// the sort of thing an exception would otherwise walk out of the process with.
    /// </summary>
    [Fact]
    public void An_unreadable_document_stops_the_host_without_quoting_it()
    {
        using var f = new Fixture();
        File.WriteAllText(f.FilePath, "{ not json");

        var thrown = Assert.Throws<InvalidOperationException>(() => new PermissionsDeploymentSource(f.FilePath, _ => null));

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
        var source = new PermissionsDeploymentSource(f.FilePath, _ => null);

        File.WriteAllText(f.FilePath, "{ broken");

        Assert.False(source.Reload());
        Assert.NotNull(source.ReloadError);
        Assert.Equal("banter", source.Resolve(Fixture.Key)!.Id);
    }

    [Fact]
    public void A_good_reload_replaces_the_policy_in_force()
    {
        using var f = new Fixture();
        var source = new PermissionsDeploymentSource(f.FilePath, _ => null);
        Assert.True(source.Reload());

        f.Document = f.Document with
        {
            Principals = [f.Document.Principals[0] with { Tools = ["kodi__play_pause"] }],
        };
        f.Save();

        Assert.True(source.Reload());
        Assert.Null(source.ReloadError);
        Assert.Equal(["kodi__play_pause"], source.Resolve(Fixture.Key)!.Tools);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Key = "mcph_a_test_key_long_enough_to_pass_validation";

        public Fixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "mcphub-perms-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(DirectoryPath);
            FilePath = Path.Combine(DirectoryPath, "permissions.json");
            File.WriteAllText(Path.Combine(DirectoryPath, "banter.key"), Key);
            Document = new PermissionsDeploymentConfiguration
            {
                AllowUnauthenticated = false,
                Principals =
                [
                    new PermissionsDeploymentPrincipal
                    {
                        Id = "banter",
                        Name = "Banter",
                        KeyFile = "banter.key",
                        Tools = ["*"],
                    },
                ],
            };
            Save();
        }

        public string DirectoryPath { get; }

        public string FilePath { get; }

        public PermissionsDeploymentConfiguration Document { get; set; }

        public void Save() => File.WriteAllText(
            FilePath, JsonSerializer.Serialize(Document, new JsonSerializerOptions { WriteIndented = true }));

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
                // A test's temp directory outliving the test is not the test's problem.
            }
        }
    }
}
