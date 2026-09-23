using System.IO.Compression;
using System.Text;
using MCPHub.Core.Backup;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Models;
using MCPHub.Core.Recipes;
using MCPHub.Core.Routing;
using MCPHub.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

/// <summary>Selective, optionally-encrypted export and import of MCPHub's configuration.</summary>
public sealed class SettingsArchiveTests
{
    [Fact]
    public async Task A_chosen_subset_round_trips_and_leaves_unchosen_settings_alone()
    {
        using var source = new Hub();
        source.Settings.Current.ProxyPort = 5999;
        source.Settings.Current.ProxyBindAddress = "0.0.0.0";
        source.Settings.Current.Theme = "Dark";
        source.Settings.Current.UserServers.Add(new UserMcpServerDefinition { DisplayName = "Mine", Endpoint = "http://localhost:1234/mcp" });
        await source.Settings.SaveAsync();

        var path = source.ArchivePath();
        await source.Archive.ExportAsync(path, [SettingsCategory.General, SettingsCategory.UserServers], password: null);

        using var target = new Hub();
        target.Settings.Current.Theme = "Light";
        var result = await target.Archive.ImportAsync(path, [SettingsCategory.General, SettingsCategory.UserServers], password: null);

        Assert.Equal([SettingsCategory.General, SettingsCategory.UserServers], result.Applied);
        Assert.Equal(5999, target.Settings.Current.ProxyPort);
        Assert.Equal("0.0.0.0", target.Settings.Current.ProxyBindAddress);
        Assert.Equal("Mine", Assert.Single(target.Settings.Current.UserServers).DisplayName);
        // Appearance was neither exported nor imported, so the target keeps its own theme.
        Assert.Equal("Light", target.Settings.Current.Theme);
    }

    [Fact]
    public async Task Importing_only_some_of_what_an_archive_holds_applies_only_that()
    {
        using var source = new Hub();
        source.Settings.Current.ProxyPort = 5999;
        source.Settings.Current.Theme = "Dark";
        var path = source.ArchivePath();
        await source.Archive.ExportAsync(path, [SettingsCategory.General, SettingsCategory.Appearance], null);

        using var target = new Hub();
        var before = target.Settings.Current.ProxyPort;
        var result = await target.Archive.ImportAsync(path, [SettingsCategory.Appearance], null);

        Assert.Equal([SettingsCategory.Appearance], result.Applied);
        Assert.Equal("Dark", target.Settings.Current.Theme);
        Assert.Equal(before, target.Settings.Current.ProxyPort);
    }

    [Fact]
    public async Task The_manifest_is_readable_without_the_password_but_the_entries_are_not()
    {
        using var source = new Hub();
        source.Settings.Current.SharedServersFolder = @"C:\a-revealing-path";
        var path = source.ArchivePath();
        await source.Archive.ExportAsync(path, [SettingsCategory.General], password: "correct horse");

        var manifest = await source.Archive.InspectAsync(path);
        Assert.True(manifest.Encrypted);
        Assert.Equal([SettingsCategory.General], manifest.Categories);
        Assert.NotNull(manifest.KdfSalt);

        // Nothing but the manifest should be legible in the zip.
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries.Where(e => e.FullName != SettingsArchiveService.ManifestEntryName))
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            Assert.DoesNotContain("a-revealing-path", reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_encrypted_archive_needs_its_password_and_says_so_when_it_is_wrong()
    {
        using var source = new Hub();
        source.Settings.Current.ProxyPort = 5999;
        var path = source.ArchivePath();
        await source.Archive.ExportAsync(path, [SettingsCategory.General], password: "correct horse");

        using var target = new Hub();
        var untouched = target.Settings.Current.ProxyPort;

        var missing = await Assert.ThrowsAsync<SettingsArchiveException>(() => target.Archive.ImportAsync(path, [SettingsCategory.General], null));
        Assert.Contains("encrypted", missing.Message);

        var wrong = await Assert.ThrowsAsync<SettingsArchiveException>(() => target.Archive.ImportAsync(path, [SettingsCategory.General], "battery staple"));
        Assert.Contains("password is wrong", wrong.Message);

        // A failed import must not have written half of itself first.
        Assert.Equal(untouched, target.Settings.Current.ProxyPort);

        await target.Archive.ImportAsync(path, [SettingsCategory.General], "correct horse");
        Assert.Equal(5999, target.Settings.Current.ProxyPort);
    }

    [Fact]
    public async Task Tokens_and_keys_refuse_to_leave_without_a_password_and_travel_when_given_one()
    {
        using var source = new Hub();
        source.Secrets.Set(SecretKeys.GithubPat, "ghp_example_token");
        var outputId = source.Router.SaveOutput(null, "Cloud", "https://api.example.com/v1", null, "sk-upstream-example");
        source.Router.SetDefault(outputId);
        var path = source.ArchivePath();

        var refused = await Assert.ThrowsAsync<SettingsArchiveException>(() =>
            source.Archive.ExportAsync(path, [SettingsCategory.Secrets], password: null));
        Assert.Contains("requires a password", refused.Message);

        await source.Archive.ExportAsync(path, [SettingsCategory.Router, SettingsCategory.Secrets], password: "pw");

        using var target = new Hub();
        await target.Archive.ImportAsync(path, [SettingsCategory.Router, SettingsCategory.Secrets], "pw");

        Assert.Equal("ghp_example_token", target.Secrets.Get(SecretKeys.GithubPat));
        var imported = Assert.Single(target.Router.Snapshot.Outputs);
        Assert.Equal("Cloud", imported.Name);
        // Re-wrapped for this machine on the way in, so it is usable and not merely copied.
        Assert.Equal("sk-upstream-example", RouterStore.ReadApiKey(imported));
        Assert.Equal(imported.Id, target.Router.Snapshot.DefaultOutputId);
    }

    [Fact]
    public async Task Router_routes_move_without_a_password_but_arrive_without_upstream_keys()
    {
        using var source = new Hub();
        var outputId = source.Router.SaveOutput(null, "Cloud", "https://api.example.com/v1", "gpt-x", "sk-upstream-example");
        var agent = source.Router.AddInput("Coding agent", outputId);
        source.Router.Configure("0.0.0.0", 5810, true);

        var path = source.ArchivePath();
        await source.Archive.ExportAsync(path, [SettingsCategory.Router], password: null);

        // The plaintext archive must not contain the key in any form.
        using (var zip = ZipFile.OpenRead(path))
        {
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                Assert.DoesNotContain("sk-upstream-example", reader.ReadToEnd(), StringComparison.Ordinal);
            }
        }

        using var target = new Hub();
        var result = await target.Archive.ImportAsync(path, [SettingsCategory.Router], null);

        var imported = Assert.Single(target.Router.Snapshot.Outputs);
        Assert.Equal("gpt-x", imported.Model);
        Assert.Null(imported.ProtectedApiKey);
        Assert.Contains(result.Notes, n => n.Contains("without an upstream key"));

        // The agent's existing key keeps working on the new machine, which is the point of moving routes.
        Assert.Equal("Coding agent", Assert.Single(target.Router.Snapshot.Inputs).Name);
        Assert.NotNull(target.Router.Resolve(agent.Key));
        Assert.Equal("0.0.0.0", target.Router.Snapshot.BindAddress);
        Assert.Equal(5810, target.Router.Snapshot.Port);
    }

    [Fact]
    public async Task Recipes_merge_by_id_and_keep_local_ones_the_archive_never_mentions()
    {
        using var source = new Hub();
        var shared = source.Recipes.Add(new RecipeDraft { Title = "Shared", When = "x", Then = "y" }, RecipeSources.User);
        source.Recipes.Add(new RecipeDraft { Title = "Only in archive", When = "x", Then = "y" }, RecipeSources.User);
        var path = source.ArchivePath();
        await source.Archive.ExportAsync(path, [SettingsCategory.Recipes], null);

        using var target = new Hub();
        target.Recipes.Add(new RecipeDraft { Title = "Only local", When = "x", Then = "y" }, RecipeSources.User);
        // Same id as the source's, with a different title: the archive's version should win for that id.
        target.Recipes.Import([new Recipe { Id = shared.Id, Title = "Stale copy", When = "x", Then = "y" }]);

        await target.Archive.ImportAsync(path, [SettingsCategory.Recipes], null);

        var titles = target.Recipes.All.Select(r => r.Title).ToList();
        Assert.Contains("Shared", titles);
        Assert.Contains("Only in archive", titles);
        Assert.Contains("Only local", titles);
        Assert.DoesNotContain("Stale copy", titles);
    }

    [Fact]
    public async Task An_archive_with_broken_routes_is_rejected_before_any_category_is_applied()
    {
        using var source = new Hub();
        source.Settings.Current.ProxyPort = 5999;
        source.Recipes.Add(new RecipeDraft { Title = "From archive", When = "x", Then = "y" }, RecipeSources.User);
        source.Router.SaveOutput(null, "Cloud", "https://api.example.com/v1", null, null);
        var path = source.ArchivePath();
        await source.Archive.ExportAsync(path, [SettingsCategory.General, SettingsCategory.Recipes, SettingsCategory.Router], null);

        // Point the default at an output the archive does not define — the shape a hand-edited archive takes.
        Corrupt(path, "settings/router.json", json => json.Replace("\"DefaultOutputId\": null", "\"DefaultOutputId\": \"missing\""));

        using var target = new Hub();
        var before = target.Settings.Current.ProxyPort;

        var ex = await Assert.ThrowsAsync<SettingsArchiveException>(() =>
            target.Archive.ImportAsync(path, [SettingsCategory.General, SettingsCategory.Recipes, SettingsCategory.Router], null));

        Assert.Contains("router routes were rejected", ex.Message);
        // Recipes and general settings come before Router in the apply order; neither may have landed.
        Assert.Empty(target.Recipes.All);
        Assert.Equal(before, target.Settings.Current.ProxyPort);
    }

    /// <summary>Rewrites one entry of an existing archive, standing in for a hand-edited file.</summary>
    private static void Corrupt(string archivePath, string entryName, Func<string, string> edit)
    {
        string original;
        using (var read = ZipFile.OpenRead(archivePath))
        using (var reader = new StreamReader(read.GetEntry(entryName)!.Open(), Encoding.UTF8))
            original = reader.ReadToEnd();

        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        zip.GetEntry(entryName)!.Delete();
        // No BOM: the archive's own entries have none, and one would fail as invalid JSON before validation.
        using var writer = new StreamWriter(zip.CreateEntry(entryName).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(edit(original));
    }

    [Fact]
    public async Task A_file_that_is_not_an_mcphub_archive_is_rejected_clearly()
    {
        using var hub = new Hub();
        var notZip = Path.Combine(hub.SettingsDirectory, "notes.txt");
        File.WriteAllText(notZip, "just some text");
        var wrongZip = hub.ArchivePath();
        using (var zip = ZipFile.Open(wrongZip, ZipArchiveMode.Create))
            zip.CreateEntry("unrelated.txt");

        Assert.Contains("not a readable zip", (await Assert.ThrowsAsync<SettingsArchiveException>(() => hub.Archive.InspectAsync(notZip))).Message);
        Assert.Contains("no manifest.json", (await Assert.ThrowsAsync<SettingsArchiveException>(() => hub.Archive.InspectAsync(wrongZip))).Message);
    }

    [Fact]
    public async Task Importing_a_category_the_archive_lacks_is_refused_rather_than_silently_doing_nothing()
    {
        using var source = new Hub();
        var path = source.ArchivePath();
        await source.Archive.ExportAsync(path, [SettingsCategory.Appearance], null);

        using var target = new Hub();
        var ex = await Assert.ThrowsAsync<SettingsArchiveException>(() => target.Archive.ImportAsync(path, [SettingsCategory.Router], null));
        Assert.Contains("None of the chosen categories", ex.Message);
    }

    /// <summary>One MCPHub's worth of stores over a throwaway settings directory.</summary>
    private sealed class Hub : IAppPaths, IDisposable
    {
        public string SettingsDirectory { get; } = Path.Combine(Path.GetTempPath(), "mcphub-archive-tests", Guid.NewGuid().ToString("N"));
        public string DataDirectory => SettingsDirectory;
        public string DownloadsDirectory => SettingsDirectory;
        public string DefaultServersDirectory => SettingsDirectory;
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }

        public SettingsStore Settings { get; }
        public SecretStore Secrets { get; }
        public RecipeStore Recipes { get; }
        public RouterStore Router { get; }
        public SettingsArchiveService Archive { get; }

        public Hub()
        {
            Directory.CreateDirectory(SettingsDirectory);
            Settings = new(this, NullLogger<SettingsStore>.Instance);
            Secrets = new(this, NullLogger<SecretStore>.Instance);
            Recipes = new(this, NullLogger<RecipeStore>.Instance);
            Router = new(this);
            Archive = new(Settings, Secrets, Recipes, Router, NullLogger<SettingsArchiveService>.Instance);
        }

        public string ArchivePath() => Path.Combine(SettingsDirectory, "export.zip");

        public void Dispose()
        {
            try { Directory.Delete(SettingsDirectory, recursive: true); } catch (IOException) { }
        }
    }
}
