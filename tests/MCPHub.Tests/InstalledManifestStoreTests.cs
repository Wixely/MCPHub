using MCPHub.Core.Services;
using Xunit;

namespace MCPHub.Tests;

public class InstalledManifestStoreTests
{
    [Fact]
    public async Task Read_missing_manifest_returns_empty()
    {
        using var dir = new TempDir();
        var store = new InstalledManifestStore();

        var map = await store.ReadAsync(dir.Path);

        Assert.Empty(map);
    }

    [Fact]
    public async Task Set_then_read_roundtrips_and_writes_under_dotmcphub()
    {
        using var dir = new TempDir();
        var store = new InstalledManifestStore();

        await store.SetVersionAsync(dir.Path, "NoteworthyMCPSharp", "1.0.2");
        await store.SetVersionAsync(dir.Path, "SQLMCPSharp", "1.1.6");

        var map = await store.ReadAsync(dir.Path);

        Assert.Equal("1.0.2", map["NoteworthyMCPSharp"]);
        Assert.Equal("1.1.6", map["SQLMCPSharp"]);
        Assert.True(File.Exists(Path.Combine(dir.Path, ".mcphub", "installed.json")));
    }

    [Fact]
    public async Task Lookup_is_case_insensitive()
    {
        using var dir = new TempDir();
        var store = new InstalledManifestStore();

        await store.SetVersionAsync(dir.Path, "NoteworthyMCPSharp", "1.0.2");
        var map = await store.ReadAsync(dir.Path);

        Assert.Equal("1.0.2", map["noteworthymcpsharp"]);
    }

    [Fact]
    public async Task Remove_deletes_entry()
    {
        using var dir = new TempDir();
        var store = new InstalledManifestStore();

        await store.SetVersionAsync(dir.Path, "NoteworthyMCPSharp", "1.0.2");
        await store.RemoveAsync(dir.Path, "NoteworthyMCPSharp");

        var map = await store.ReadAsync(dir.Path);

        Assert.False(map.ContainsKey("NoteworthyMCPSharp"));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcphub-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }
}
