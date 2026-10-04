using System.Text.Json;
using MCPHub.Core.Users;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// One identity per caller, usable on every surface.
///
/// <para>The Router and the proxy each used to keep their own callers with their own keys on their own
/// listeners, so an agent wanting a model route and a tool needed two of everything. These pin the
/// shared directory that replaces both — and especially the properties that made it worth doing: one
/// key, and one switch that stops it everywhere.</para>
/// </summary>
public sealed class UserDirectoryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private UserStore Store() => new(new FakeAppPaths(_dir.Path));

    private string FilePath => Path.Combine(_dir.Path, "users.json");

    // ---- keys ----------------------------------------------------------------------------------

    [Fact]
    public void A_new_user_can_use_the_key_it_was_handed()
    {
        var store = Store();

        var (user, key) = store.Create("Banter");

        Assert.Equal(user.Id, store.Resolve(key)!.Id);
        Assert.Equal("Banter", store.Resolve(key)!.Name);
    }

    [Fact]
    public void The_key_is_never_written_down()
    {
        var store = Store();
        var (_, key) = store.Create("Banter");

        Assert.DoesNotContain(key, File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Contains(UserKeys.Hash(key), File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Rotating_issues_a_new_key_and_retires_the_old()
    {
        var store = Store();
        var (user, first) = store.Create("Banter");

        var second = store.RotateKey(user.Id);

        Assert.NotEqual(first, second);
        Assert.Null(store.Resolve(first));
        Assert.Equal(user.Id, store.Resolve(second)!.Id);
    }

    /// <summary>
    /// The reason the floor is 32 rather than something rounder: keys the Router issued before users
    /// existed are 73 characters of <c>mhrouter_</c> plus hex, and they have to keep working once
    /// migrated. Only the hash is ever compared, so the prefix is a label for people.
    /// </summary>
    [Fact]
    public void A_key_issued_by_an_older_version_still_resolves()
    {
        var legacy = "mhrouter_" + new string('a', 64);
        Assert.True(legacy.Length >= UserKeys.MinimumLength);

        var directory = new StaticUserDirectory(new HubUsersConfiguration
        {
            Users = [new HubUser { Id = "migrated", Name = "Old agent", KeyHash = UserKeys.Hash(legacy) }],
        });

        Assert.Equal("migrated", directory.Resolve(legacy)!.Id);
    }

    [Fact]
    public void Generated_keys_are_unique_long_and_need_no_escaping()
    {
        var keys = Enumerable.Range(0, 64).Select(_ => UserKeys.New()).ToList();

        Assert.Equal(64, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.StartsWith("mcphub_", k, StringComparison.Ordinal));
        Assert.All(keys, k => Assert.True(k.Length >= UserKeys.MinimumLength, $"'{k}' is too short"));
        Assert.All(keys, k => Assert.True(
            k.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'), $"'{k}' needs escaping"));
    }

    // ---- one switch, every surface -------------------------------------------------------------

    /// <summary>
    /// The point of the whole refactor: suspending a caller is one action, and its key stops being
    /// recognised at all rather than on one listener.
    /// </summary>
    [Fact]
    public void Disabling_a_user_stops_its_key_being_recognised_anywhere()
    {
        var store = Store();
        var (user, key) = store.Create("agent");

        store.SetEnabled(user.Id, false);
        Assert.Null(store.Resolve(key));

        store.SetEnabled(user.Id, true);
        Assert.Equal(user.Id, store.Resolve(key)!.Id);
    }

    [Fact]
    public void Deleting_a_user_retires_its_key()
    {
        var store = Store();
        var (user, key) = store.Create("agent");

        store.Delete(user.Id);

        Assert.Null(store.Resolve(key));
        Assert.Empty(store.Snapshot.Users);
    }

    [Fact]
    public void Editing_a_user_that_is_not_there_is_refused_by_name()
    {
        var store = Store();

        Assert.Equal("ghost", Assert.Throws<UserNotFoundException>(() => store.SetEnabled("ghost", false)).Id);
        Assert.Throws<UserNotFoundException>(() => store.RotateKey("ghost"));
        Assert.Throws<UserNotFoundException>(() => store.Rename("ghost", "x"));
        Assert.Throws<UserNotFoundException>(() => store.Delete("ghost"));
    }

    [Fact]
    public void Two_users_never_share_a_key()
    {
        var store = Store();

        var (_, first) = store.Create("one");
        var (_, second) = store.Create("two");

        Assert.NotEqual(store.Resolve(first)!.Id, store.Resolve(second)!.Id);

        // And a key that already belongs to somebody cannot be adopted onto a second identity, which is
        // the one way a duplicate could otherwise get in — migration and import both arrive that way.
        Assert.Throws<ArgumentException>(
            () => store.Adopt(new HubUser { Id = "other", Name = "Other", KeyHash = UserKeys.Hash(first) }));
    }

    // ---- persistence ---------------------------------------------------------------------------

    [Fact]
    public void Users_survive_a_restart()
    {
        var (user, key) = Store().Create("Banter");

        var reopened = Store();

        Assert.Equal(user.Id, reopened.Resolve(key)!.Id);
        Assert.Equal("Banter", reopened.Resolve(key)!.Name);
    }

    [Fact]
    public void Nothing_is_written_until_the_first_user()
    {
        _ = Store();

        Assert.False(File.Exists(FilePath), "an untouched hub should not create a users file");
    }

    /// <summary>
    /// A corrupt directory must not stop the hub launching, must not be overwritten — somebody may
    /// still want to repair it — and must recognise nobody while it stands. An empty directory
    /// authenticates nobody, which is the only direction this is allowed to fail in.
    /// </summary>
    [Fact]
    public void A_corrupt_directory_recognises_nobody_and_refuses_to_be_edited()
    {
        var (_, key) = Store().Create("Banter");
        File.WriteAllText(FilePath, "{ not json");

        var store = Store();

        Assert.NotNull(store.LoadError);
        Assert.Null(store.Resolve(key));
        Assert.Empty(store.Snapshot.Users);
        Assert.Throws<InvalidOperationException>(() => store.Create("another"));
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void A_rejected_change_leaves_the_stored_users_alone()
    {
        var store = Store();
        var (user, key) = store.Create("agent");

        Assert.Throws<ArgumentException>(() => store.Rename(user.Id, new string('x', 200)));

        Assert.Equal("agent", store.Resolve(key)!.Name);
        Assert.Equal("agent", Store().Resolve(key)!.Name);
    }

    // ---- the mounted document ------------------------------------------------------------------

    [Fact]
    public void A_mounted_document_resolves_a_key_from_a_file_without_the_key_being_in_it()
    {
        using var f = new Fixture(_dir.Path);

        var source = new UsersDeploymentSource(f.FilePath, _ => null);

        Assert.Equal("banter", source.Resolve(Fixture.Key)!.Id);
        Assert.DoesNotContain(Fixture.Key, File.ReadAllText(f.FilePath), StringComparison.Ordinal);
        Assert.Null(source.ReloadError);
    }

    [Fact]
    public void A_key_can_come_from_the_environment_instead()
    {
        using var f = new Fixture(_dir.Path);
        f.Document = f.Document with
        {
            Users = [f.Document.Users[0] with { KeyFile = null, KeyEnvironmentVariable = "BANTER_HUB_KEY" }],
        };
        f.Save();

        var source = new UsersDeploymentSource(f.FilePath, name => name == "BANTER_HUB_KEY" ? Fixture.Key : null);

        Assert.Equal("banter", source.Resolve(Fixture.Key)!.Id);
    }

    [Fact]
    public void Naming_two_credential_sources_for_one_user_is_refused()
    {
        using var f = new Fixture(_dir.Path);
        f.Document = f.Document with { Users = [f.Document.Users[0] with { KeyHash = UserKeys.Hash(Fixture.Key) }] };
        f.Save();

        Assert.Throws<InvalidOperationException>(() => new UsersDeploymentSource(f.FilePath, _ => null));
    }

    [Fact]
    public void An_unreadable_document_stops_the_host_without_quoting_it()
    {
        using var f = new Fixture(_dir.Path);
        File.WriteAllText(f.FilePath, "{ not json");

        var thrown = Assert.Throws<InvalidOperationException>(() => new UsersDeploymentSource(f.FilePath, _ => null));

        Assert.DoesNotContain("not json", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(f.FilePath, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_reload_keeps_the_users_already_in_force()
    {
        using var f = new Fixture(_dir.Path);
        var source = new UsersDeploymentSource(f.FilePath, _ => null);

        File.WriteAllText(f.FilePath, "{ broken");

        Assert.False(source.Reload());
        Assert.NotNull(source.ReloadError);
        Assert.Equal("banter", source.Resolve(Fixture.Key)!.Id);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Key = "mcphub_a_test_key_long_enough_to_pass_validation";

        private readonly string _directory;

        public Fixture(string parent)
        {
            _directory = Path.Combine(parent, "deployment");
            Directory.CreateDirectory(_directory);
            FilePath = Path.Combine(_directory, "users.json");
            File.WriteAllText(Path.Combine(_directory, "banter.key"), Key);
            Document = new UsersDeploymentConfiguration
            {
                Users = [new UsersDeploymentUser { Id = "banter", Name = "Banter", KeyFile = "banter.key" }],
            };
            Save();
        }

        public string FilePath { get; }

        public UsersDeploymentConfiguration Document { get; set; }

        public void Save() => File.WriteAllText(
            FilePath, JsonSerializer.Serialize(Document, new JsonSerializerOptions { WriteIndented = true }));

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory outliving its test is not the test's problem.
            }
        }
    }
}
