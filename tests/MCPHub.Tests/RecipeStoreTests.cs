using MCPHub.Core.Infrastructure;
using MCPHub.Core.Recipes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

public class RecipeStoreTests
{
    private static RecipeStore NewStore(string dir) => new(new FakeAppPaths(dir), NullLogger<RecipeStore>.Instance);

    private static RecipeDraft KodiDraft() => new()
    {
        Title = "Access Kodi",
        When = "kodi tools fail because Kodi is not running",
        Then = "On Android launch Kodi with adb__launch_app; on Windows/Linux start it with remoteadmin; then retry.",
        Services = ["Kodi", "adb", " remoteadmin ", "adb"],
        Notes = "Give it ~5s to start.",
    };

    [Fact]
    public void Empty_when_no_file()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);

        Assert.Empty(store.All);
        Assert.Equal(Path.Combine(dir.Path, RecipeStore.FileName), store.FilePath);
        Assert.False(File.Exists(store.FilePath), "Nothing should be written until the first mutation.");
    }

    [Fact]
    public void Add_assigns_id_normalises_services_and_persists_across_instances()
    {
        using var dir = new TempDir();

        var added = NewStore(dir.Path).Add(KodiDraft(), RecipeSources.Agent);

        Assert.Matches("^[0-9a-f]{8}$", added.Id);
        Assert.Equal(["kodi", "adb", "remoteadmin"], added.Services);
        Assert.Equal(RecipeSources.Agent, added.Source);
        Assert.Equal(added.CreatedAt, added.UpdatedAt);

        var reloaded = NewStore(dir.Path);
        var recipe = Assert.Single(reloaded.All);
        Assert.Equal(added.Id, recipe.Id);
        Assert.Equal("Access Kodi", recipe.Title);
        Assert.Equal(added.Then, recipe.Then);
        Assert.Equal(["kodi", "adb", "remoteadmin"], recipe.Services);
        Assert.Equal("Give it ~5s to start.", recipe.Notes);
    }

    [Fact]
    public void Update_replaces_fields_bumps_timestamp_and_returns_null_for_unknown_id()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        var added = store.Add(KodiDraft(), RecipeSources.User);

        var draft = RecipeDraft.From(added);
        draft.Then = "Use adb__launch_app.";
        draft.Notes = null;
        var updated = store.Update(added.Id.ToUpperInvariant(), draft, RecipeSources.Agent);

        Assert.NotNull(updated);
        Assert.Equal("Use adb__launch_app.", updated!.Then);
        Assert.Null(updated.Notes);
        Assert.Equal(RecipeSources.Agent, updated.Source);
        Assert.True(updated.UpdatedAt >= added.UpdatedAt);
        Assert.Equal(added.CreatedAt, updated.CreatedAt);

        Assert.Null(store.Update("nope", draft, RecipeSources.User));
    }

    [Fact]
    public void Remove_deletes_and_reports_missing()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        var added = store.Add(KodiDraft(), RecipeSources.User);

        Assert.True(store.Remove(added.Id));
        Assert.False(store.Remove(added.Id));
        Assert.Empty(store.All);
        Assert.Empty(NewStore(dir.Path).All);
    }

    [Fact]
    public void Changed_fires_once_per_mutation()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        var fired = 0;
        store.Changed += () => fired++;

        var added = store.Add(KodiDraft(), RecipeSources.User);
        store.Update(added.Id, RecipeDraft.From(added), RecipeSources.User);
        store.Remove(added.Id);
        store.Remove(added.Id); // no-op: must not fire

        Assert.Equal(3, fired);
    }

    [Fact]
    public void Snapshots_are_copies()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        var added = store.Add(KodiDraft(), RecipeSources.User);

        added.Title = "tampered";
        added.Services.Add("rogue");

        var fresh = store.Find(added.Id)!;
        Assert.Equal("Access Kodi", fresh.Title);
        Assert.DoesNotContain("rogue", fresh.Services);
    }

    [Theory]
    [InlineData(null, "when", "then", "'title' is required.")]
    [InlineData("   ", "when", "then", "'title' is required.")]
    [InlineData("title", "", "then", "'when' is required.")]
    [InlineData("title", "when", null, "'then' is required.")]
    public void Validation_requires_title_when_and_then(string? title, string? when, string? then, string expected)
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);

        var ex = Assert.Throws<RecipeValidationException>(() =>
            store.Add(new RecipeDraft { Title = title, When = when, Then = then }, RecipeSources.User));

        Assert.Equal(expected, ex.Message);
        Assert.Empty(store.All);
    }

    [Fact]
    public void Validation_keeps_recipes_concise()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);

        var ex = Assert.Throws<RecipeValidationException>(() => store.Add(new RecipeDraft
        {
            Title = "t",
            When = "w",
            Then = new string('x', RecipeValidator.MaxThenLength + 1),
        }, RecipeSources.User));

        Assert.Contains("'then' is too long", ex.Message);
        Assert.Contains("concise", ex.Message);
    }

    [Fact]
    public void Title_is_collapsed_to_one_line()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);

        var added = store.Add(new RecipeDraft { Title = "Access\r\n  Kodi ", When = "w", Then = "t" }, RecipeSources.User);

        Assert.Equal("Access Kodi", added.Title);
    }

    [Fact]
    public void Search_matches_any_field_and_filters_by_service()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        store.Add(KodiDraft(), RecipeSources.User);
        store.Add(new RecipeDraft
        {
            Title = "Paperless needs the consume folder",
            When = "paperlessngx__upload fails with a path error",
            Then = "Copy the file into the consume folder with remoteadmin first.",
            Services = ["paperlessngx", "remoteadmin"],
        }, RecipeSources.User);

        Assert.Equal(2, store.Search(null).Count);
        Assert.Equal(2, store.Search("REMOTEADMIN").Count);                       // service text
        Assert.Equal("Access Kodi", Assert.Single(store.Search("android")).Title); // then text
        Assert.Equal("Access Kodi", Assert.Single(store.Search("~5s")).Title);     // notes text
        Assert.Equal(2, store.Search(null, " RemoteAdmin ").Count);               // service filter, normalised
        Assert.Equal("Access Kodi", Assert.Single(store.Search(null, "kodi")).Title);
        Assert.Single(store.Search("consume", "remoteadmin"));
        Assert.Empty(store.Search("consume", "kodi"));
    }

    [Fact]
    public void All_is_ordered_by_title()
    {
        using var dir = new TempDir();
        var store = NewStore(dir.Path);
        store.Add(new RecipeDraft { Title = "zebra", When = "w", Then = "t" }, RecipeSources.User);
        store.Add(new RecipeDraft { Title = "Apple", When = "w", Then = "t" }, RecipeSources.User);
        store.Add(new RecipeDraft { Title = "mango", When = "w", Then = "t" }, RecipeSources.User);

        Assert.Equal(["Apple", "mango", "zebra"], store.All.Select(r => r.Title));
    }

    [Fact]
    public void Corrupt_file_is_quarantined_not_overwritten()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, RecipeStore.FileName);
        File.WriteAllText(path, "{ this is not json");

        var store = NewStore(dir.Path);

        Assert.Empty(store.All);
        Assert.False(File.Exists(path));
        var quarantined = Assert.Single(Directory.GetFiles(dir.Path, RecipeStore.FileName + ".corrupt-*"));
        Assert.Equal("{ this is not json", File.ReadAllText(quarantined));
    }

    [Fact]
    public void Hand_edited_entries_without_id_or_title_are_dropped_but_the_rest_survive()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, RecipeStore.FileName), """
            {
              "schemaVersion": 1,
              "recipes": [
                { "id": "abcd1234", "title": "Good", "when": "w", "then": "t", "services": ["Kodi", "KODI"] },
                { "title": "No id", "when": "w", "then": "t" }
              ]
            }
            """);

        var store = NewStore(dir.Path);

        var recipe = Assert.Single(store.All);
        Assert.Equal("Good", recipe.Title);
        Assert.Equal(["kodi"], recipe.Services);
    }

    internal sealed class FakeAppPaths(string dir) : IAppPaths
    {
        public string SettingsDirectory => dir;
        public string DataDirectory => dir;
        public string DownloadsDirectory => Path.Combine(dir, "downloads");
        public string DefaultServersDirectory => Path.Combine(dir, "servers");
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
    }

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcphub-recipes-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
