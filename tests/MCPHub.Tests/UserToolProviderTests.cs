using System.Text.Json;
using MCPHub.Core.Permissions;
using MCPHub.Core.Routing;
using MCPHub.Core.Users;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Xunit;
using FakeAppPaths = MCPHub.Tests.RecipeStoreTests.FakeAppPaths;
using TempDir = MCPHub.Tests.RecipeStoreTests.TempDir;

namespace MCPHub.Tests;

/// <summary>
/// Identity as an API: issuing keys, suspending them, and retiring them from another process.
///
/// <para>A management UI that can grant tools but cannot create the user to grant them to is no use,
/// so these tools exist alongside <c>permissions__*</c> — and behind the same switch, because between
/// them they are the hub's administration.</para>
/// </summary>
public sealed class UserToolProviderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private UserStore Store() => new(new FakeAppPaths(_dir.Path));

    private PermissionsStore Permissions() => new(new FakeAppPaths(_dir.Path));

    private UserToolProvider Provider(IUserDirectory users, params IUserDependent[] dependents) =>
        new(users, dependents, NullLogger<UserToolProvider>.Instance);

    private static async Task<JsonElement> CallAsync(
        UserToolProvider provider, string tool, object? arguments = null)
    {
        var result = await provider.CallAsync(tool, Arguments(arguments), CancellationToken.None);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.False(result.IsError ?? false, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(bool IsError, JsonElement Body)> TryCallAsync(
        UserToolProvider provider, string tool, object? arguments = null)
    {
        var result = await provider.CallAsync(tool, Arguments(arguments), CancellationToken.None);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return (result.IsError ?? false, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static Dictionary<string, JsonElement>? Arguments(object? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments));
    }

    // ---- issuing keys --------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_user_hands_back_a_working_key_once()
    {
        var store = Store();
        var provider = Provider(store);

        var created = await CallAsync(provider, "create", new { name = "Banter" });
        var key = created.GetProperty("key").GetString()!;

        Assert.NotEmpty(key);
        Assert.NotEmpty(created.GetProperty("notice").GetString()!);
        Assert.Equal("Banter", store.Resolve(key)!.Name);

        // And nothing else will ever give it back.
        Assert.DoesNotContain(key, (await CallAsync(provider, "list")).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_users_shows_a_fingerprint_rather_than_anything_usable()
    {
        var store = Store();
        store.Create("Banter");

        var listed = await CallAsync(Provider(store), "list");

        Assert.True(listed.GetProperty("editable").GetBoolean());
        var user = Assert.Single(listed.GetProperty("users").EnumerateArray().ToList());
        Assert.Equal("Banter", user.GetProperty("name").GetString());
        Assert.True(user.GetProperty("enabled").GetBoolean());
        Assert.Equal(8, user.GetProperty("keyFingerprint").GetString()!.Length);
    }

    [Fact]
    public async Task Rotating_a_key_retires_the_previous_one()
    {
        var store = Store();
        var provider = Provider(store);
        var first = (await CallAsync(provider, "create", new { name = "Banter" })).GetProperty("key").GetString()!;

        var second = (await CallAsync(provider, "rotate_key", new { user = "Banter" })).GetProperty("key").GetString()!;

        Assert.Null(store.Resolve(first));
        Assert.NotNull(store.Resolve(second));
    }

    // ---- editing -------------------------------------------------------------------------------

    [Fact]
    public async Task A_user_can_be_renamed_without_touching_its_key()
    {
        var store = Store();
        var issued = store.Create("agent");

        var changed = await CallAsync(Provider(store), "rename", new { user = "agent", name = "renamed" });

        Assert.Equal("renamed", store.Resolve(issued.Key)!.Name);
        Assert.Equal("renamed", changed.GetProperty("user").GetProperty("name").GetString());
    }

    /// <summary>
    /// One switch stops everything, and to the caller it is indistinguishable from a key that was
    /// never issued — a caller learning its key is recognised but switched off has learnt it has a
    /// valid key.
    /// </summary>
    [Fact]
    public async Task Suspending_a_user_stops_its_key_everywhere_at_once()
    {
        var store = Store();
        var issued = store.Create("agent");
        var provider = Provider(store);

        await CallAsync(provider, "set_enabled", new { user = "agent", enabled = false });

        Assert.Null(store.Resolve(issued.Key));
        Assert.False((await CallAsync(provider, "list")).GetProperty("users")[0].GetProperty("enabled").GetBoolean());

        await CallAsync(provider, "set_enabled", new { user = "agent", enabled = true });

        Assert.NotNull(store.Resolve(issued.Key));
    }

    /// <summary>
    /// Deleting takes everything keyed to the user with it, so nothing is left naming an id nobody
    /// holds — and a user created next starts with nothing rather than inheriting.
    /// </summary>
    [Fact]
    public async Task Deleting_a_user_retires_its_key_and_drops_everything_keyed_to_it()
    {
        var store = Store();
        var permissions = Permissions();
        var router = new RouterStore(new FakeAppPaths(_dir.Path), store);
        var issued = store.Create("agent");
        permissions.SetGrants(issued.User.Id, ["*"]);
        router.SetRoute(issued.User.Id, null);

        var changed = await CallAsync(Provider(store, permissions, router), "delete", new { user = "agent" });

        Assert.Null(store.Resolve(issued.Key));
        Assert.Empty(store.Snapshot.Users);
        Assert.Empty(permissions.Snapshot.Grants);
        Assert.Empty(router.Snapshot.Inputs);
        Assert.Contains("routes", changed.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hub whose grants and routes are mounted read-only still deletes the user: the key has to stop
    /// working. The entries it leaves behind are inert and reported as such, which is better than a live
    /// key with no owner.
    /// </summary>
    [Fact]
    public async Task A_user_can_be_deleted_even_when_its_grants_cannot_be()
    {
        var store = Store();
        var issued = store.Create("agent");

        await CallAsync(Provider(store), "delete", new { user = "agent" });

        Assert.Null(store.Resolve(issued.Key));
    }

    // ---- errors a caller can act on ------------------------------------------------------------

    /// <summary>
    /// A container whose users are a mounted document cannot issue keys, and a client has to be told
    /// so by name rather than find out by having a call appear to work.
    /// </summary>
    [Fact]
    public async Task Editing_a_read_only_directory_is_refused_by_name()
    {
        var mounted = new StaticUserDirectory(new HubUsersConfiguration());
        var provider = Provider(mounted);

        var (isError, body) = await TryCallAsync(provider, "create", new { name = "nope" });

        Assert.True(isError);
        Assert.Equal(UserToolProvider.ReadOnlyCode, body.GetProperty("code").GetString());
        Assert.NotNull(body.GetProperty("remedy").GetString());

        // And list says the same thing up front, so a UI never has to find out by failing.
        Assert.False((await CallAsync(provider, "list")).GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_user_is_refused_by_name()
    {
        var (isError, body) = await TryCallAsync(Provider(Store()), "rotate_key", new { user = "ghost" });

        Assert.True(isError);
        Assert.Equal(UserToolProvider.NoSuchUserCode, body.GetProperty("code").GetString());
    }

    /// <summary>
    /// Two users sharing a name must not be resolved arbitrarily: suspending the wrong one of two
    /// callers called "agent" and reporting success is worse than refusing.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_name_is_refused_rather_than_guessed()
    {
        var store = Store();
        store.Create("agent");
        store.Create("agent");

        var (isError, body) = await TryCallAsync(
            Provider(store), "set_enabled", new { user = "agent", enabled = false });

        Assert.True(isError);
        Assert.Equal(UserToolProvider.AmbiguousUserCode, body.GetProperty("code").GetString());
        Assert.Contains("id", body.GetProperty("remedy").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Both still work, because nothing was guessed at.
        Assert.Equal(2, store.Snapshot.Users.Count(u => u.Enabled));
    }

    /// <summary>A user is always addressable by id, which is what the refusal above tells a caller to
    /// use.</summary>
    [Fact]
    public async Task An_ambiguous_name_can_be_resolved_by_using_the_id()
    {
        var store = Store();
        var first = store.Create("agent");
        store.Create("agent");

        await CallAsync(Provider(store), "set_enabled", new { user = first.User.Id, enabled = false });

        Assert.Null(store.Resolve(first.Key));
        Assert.Single(store.Snapshot.Users, u => u.Enabled);
    }

    [Fact]
    public async Task A_missing_argument_is_refused_before_the_user_is_looked_up()
    {
        var (isError, body) = await TryCallAsync(Provider(Store()), "set_enabled", new { user = "ghost" });

        Assert.True(isError);
        Assert.Equal("users.bad_argument", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_name_that_cannot_be_stored_is_refused_with_a_code()
    {
        var (isError, body) = await TryCallAsync(Provider(Store()), "create", new { name = new string('x', 200) });

        Assert.True(isError);
        Assert.Equal("users.invalid", body.GetProperty("code").GetString());
    }

    /// <summary>Nobody can be edited while the directory cannot be read, and the refusal says which
    /// file to repair rather than reporting a generic failure.</summary>
    [Fact]
    public async Task A_corrupt_directory_refuses_every_edit_by_name()
    {
        Directory.CreateDirectory(_dir.Path);
        File.WriteAllText(Path.Combine(_dir.Path, "users.json"), "{ not json");
        var provider = Provider(Store());

        var (isError, body) = await TryCallAsync(provider, "create", new { name = "agent" });

        Assert.True(isError);
        Assert.Equal(UserToolProvider.UnreadableCode, body.GetProperty("code").GetString());
        Assert.NotNull((await CallAsync(provider, "list")).GetProperty("loadError").GetString());
    }

    [Fact]
    public async Task An_unknown_tool_name_is_refused_with_a_code()
    {
        var (isError, body) = await TryCallAsync(Provider(Store()), "not_a_tool");

        Assert.True(isError);
        Assert.Equal("users.unknown_tool", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Every_declared_tool_is_implemented()
    {
        var provider = Provider(Store());

        foreach (var tool in provider.Tools)
        {
            var (_, body) = await TryCallAsync(provider, tool.Name);
            Assert.NotEqual("users.unknown_tool", body.TryGetProperty("code", out var code) ? code.GetString() : null);
        }
    }
}
