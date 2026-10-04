using System.Security.Cryptography;
using System.Text;
using MCPHub.Core.Routing;
using MCPHub.Core.Users;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;

namespace MCPHub.Tests;

/// <summary>
/// Moving the Router's own agents into the user directory.
///
/// <para>This is the part of the change that must cost nobody anything. Every agent configured on the
/// Router page before users existed holds a key the Router issued; routes now name users, so unless
/// those agents arrive in the directory with their hashes and ids intact, upgrading silently revokes
/// every one of them. So what is pinned here is: the same key still works, the same id is still used,
/// and nothing is written to the file until something else saves.</para>
/// </summary>
public sealed class RouterMigrationTests : IDisposable
{
    private const string LegacyKey = "mhrouter_a_key_the_old_router_issued_0123456789";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mcphub-router-migration", Guid.NewGuid().ToString("N"));

    public RouterMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test's temp directory outliving the test is not the test's problem.
        }
    }

    private string RouterPath => Path.Combine(_directory, "router.json");

    private static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private void WriteLegacy(string? outputId = "local", bool enabled = true, string name = "Coding agent") =>
        File.WriteAllText(RouterPath, $$"""
            {
              "SchemaVersion": 1,
              "Port": 5801,
              "BindAddress": "127.0.0.1",
              "StartOnLaunch": false,
              "DefaultOutputId": "local",
              "Inputs": [
                {
                  "Id": "legacy-agent-id",
                  "Name": "{{name}}",
                  "KeyHash": "{{Hash(LegacyKey)}}",
                  "Enabled": {{(enabled ? "true" : "false")}},
                  "OutputId": {{(outputId is null ? "null" : $"\"{outputId}\"")}}
                }
              ],
              "Outputs": [
                { "Id": "local", "Name": "Local", "BaseUrl": "http://127.0.0.1:8000/v1/" }
              ]
            }
            """);

    private (RouterStore Store, UserStore Users) Open()
    {
        var paths = new FakeAppPaths(_directory);
        var users = new UserStore(paths);
        return (new RouterStore(paths, users), users);
    }

    // ---- the key keeps working ------------------------------------------------------------------

    [Fact]
    public void A_legacy_agents_key_still_works_and_still_reaches_its_output()
    {
        WriteLegacy();

        var (store, users) = Open();

        Assert.Null(store.LoadError);
        var route = store.Resolve(LegacyKey);
        Assert.NotNull(route);
        Assert.Equal("local", route.Output!.Id);

        // The id comes across too, so anything keyed to the old input — its activity history, say —
        // still points at the same caller.
        Assert.Equal("legacy-agent-id", route.UserId);
        Assert.Equal("legacy-agent-id", Assert.Single(users.Snapshot.Users).Id);
        Assert.Equal("Coding agent", users.Snapshot.Users[0].Name);
    }

    /// <summary>And the key now works on the proxy as well, which is the whole point of the move.</summary>
    [Fact]
    public void A_migrated_key_resolves_through_the_user_directory_itself()
    {
        WriteLegacy();

        var (_, users) = Open();

        Assert.Equal("legacy-agent-id", users.Resolve(LegacyKey)!.Id);
    }

    [Fact]
    public void A_disabled_legacy_agent_arrives_suspended_rather_than_live()
    {
        WriteLegacy(enabled: false);

        var (store, users) = Open();

        Assert.False(Assert.Single(users.Snapshot.Users).Enabled);
        Assert.Null(store.Resolve(LegacyKey));

        // Its route is intact, so enabling the user restores exactly what it had.
        Assert.Equal("legacy-agent-id", Assert.Single(store.Snapshot.Inputs).UserId);
        users.SetEnabled("legacy-agent-id", true);
        Assert.NotNull(store.Resolve(LegacyKey));
    }

    [Fact]
    public void An_agent_with_no_name_gets_one_rather_than_being_refused()
    {
        WriteLegacy(name: " ");

        var (store, users) = Open();

        Assert.Null(store.LoadError);
        Assert.NotEmpty(Assert.Single(users.Snapshot.Users).Name);
    }

    // ---- when it runs, and when it does not -----------------------------------------------------

    /// <summary>
    /// Reading a file must not rewrite it: the in-memory routes are already correct, and a load that
    /// changes the document on disk is a surprise if anything then goes wrong. The next save records it.
    /// </summary>
    [Fact]
    public void Migrating_does_not_touch_the_file_until_something_else_saves()
    {
        WriteLegacy();
        var before = File.ReadAllText(RouterPath);

        var (store, _) = Open();

        Assert.Equal(before, File.ReadAllText(RouterPath));

        store.SetDefault(null);

        var after = File.ReadAllText(RouterPath);
        Assert.DoesNotContain("KeyHash", after, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legacy-agent-id", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Reopening_a_migrated_document_migrates_nothing_and_changes_nobody()
    {
        WriteLegacy();
        var (first, users) = Open();
        first.SetDefault("local");
        var migrated = File.ReadAllText(RouterPath);

        var (second, reopened) = Open();

        Assert.Equal(migrated, File.ReadAllText(RouterPath));
        Assert.Single(reopened.Snapshot.Users);
        Assert.NotNull(second.Resolve(LegacyKey));
        Assert.Equal(users.Snapshot.Users[0], reopened.Snapshot.Users[0]);
    }

    /// <summary>
    /// A key the directory already holds is matched to that user rather than adopted twice — otherwise
    /// migrating on a machine where the same key had already been imported would mint a second identity
    /// for one credential, and resolution would pick between them arbitrarily.
    /// </summary>
    [Fact]
    public void An_agent_whose_key_the_directory_already_holds_is_not_adopted_twice()
    {
        var paths = new FakeAppPaths(_directory);
        var users = new UserStore(paths);
        users.Adopt(new HubUser { Id = "already-here", Name = "Already here", KeyHash = Hash(LegacyKey) });
        WriteLegacy();

        var store = new RouterStore(paths, users);

        Assert.Single(users.Snapshot.Users);
        Assert.Equal("already-here", Assert.Single(store.Snapshot.Inputs).UserId);
        Assert.Equal("already-here", store.Resolve(LegacyKey)!.UserId);
    }

    /// <summary>
    /// An id taken by an unrelated user gets a fresh one instead, so both keys keep working rather than
    /// one quietly displacing the other.
    /// </summary>
    [Fact]
    public void An_agent_whose_id_is_taken_by_somebody_else_is_adopted_under_a_new_id()
    {
        var paths = new FakeAppPaths(_directory);
        var users = new UserStore(paths);
        var (existing, existingKey) = users.Create("Unrelated");
        File.WriteAllText(RouterPath, $$"""
            {
              "SchemaVersion": 1,
              "Port": 5801,
              "Inputs": [
                { "Id": "{{existing.Id}}", "Name": "Legacy", "KeyHash": "{{Hash(LegacyKey)}}", "Enabled": true }
              ],
              "Outputs": []
            }
            """);

        var store = new RouterStore(paths, users);

        Assert.Equal(2, users.Snapshot.Users.Length);
        Assert.NotNull(users.Resolve(existingKey));
        Assert.NotNull(users.Resolve(LegacyKey));
        Assert.NotEqual(existing.Id, Assert.Single(store.Snapshot.Inputs).UserId);
    }

    /// <summary>An input with no hash could never authenticate anybody, so it is dropped rather than
    /// turned into a route for a user with no key.</summary>
    [Fact]
    public void An_input_with_no_key_at_all_is_dropped_rather_than_adopted()
    {
        File.WriteAllText(RouterPath, """
            {
              "SchemaVersion": 1,
              "Port": 5801,
              "Inputs": [{ "Id": "no-key", "Name": "Broken", "Enabled": true }],
              "Outputs": []
            }
            """);

        var (store, users) = Open();

        Assert.Null(store.LoadError);
        Assert.Empty(store.Snapshot.Inputs);
        Assert.Empty(users.Snapshot.Users);
    }

    /// <summary>
    /// A document that cannot be migrated leaves the hub as a corrupt one would: nobody routed, nothing
    /// overwritten, and the page saying so. Granting nothing is the only safe way for this to fail.
    /// </summary>
    [Fact]
    public void A_directory_that_refuses_the_migration_leaves_the_router_reporting_rather_than_routing()
    {
        WriteLegacy();
        var paths = new FakeAppPaths(_directory);
        File.WriteAllText(Path.Combine(_directory, "users.json"), "{ not json");

        var store = new RouterStore(paths, new UserStore(paths));

        Assert.NotNull(store.LoadError);
        Assert.Null(store.Resolve(LegacyKey));
        Assert.Contains("KeyHash", File.ReadAllText(RouterPath), StringComparison.OrdinalIgnoreCase);
    }
}
