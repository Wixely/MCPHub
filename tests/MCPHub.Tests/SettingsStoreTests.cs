using MCPHub.Core.Infrastructure;
using MCPHub.Core.Models;
using MCPHub.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_settings_returns_defaults()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(new FakeAppPaths(dir.Path), NullLogger<SettingsStore>.Instance);

        Assert.Equal(5800, store.Current.ProxyPort);
        Assert.Equal(PublishFlavor.SelfContained, store.Current.Flavor);
        Assert.Equal("127.0.0.1", store.Current.ProxyBindAddress);
    }

    [Fact]
    public async Task Settings_roundtrip_across_instances()
    {
        using var dir = new TempDir();
        var paths = new FakeAppPaths(dir.Path);

        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        store.Current.ProxyPort = 5810;
        store.Current.Theme = "Dark";
        store.Current.Flavor = PublishFlavor.FrameworkDependent;
        store.Current.UserServers.Add(new UserMcpServerDefinition
        {
            DisplayName = "My HTTP server",
            Kind = McpTransportKind.Http,
            Endpoint = "http://localhost:1234/mcp",
        });
        await store.SaveAsync();

        var reloaded = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        Assert.Equal(5810, reloaded.Current.ProxyPort);
        Assert.Equal("Dark", reloaded.Current.Theme);
        Assert.Equal(PublishFlavor.FrameworkDependent, reloaded.Current.Flavor);
        var userServer = Assert.Single(reloaded.Current.UserServers);
        Assert.Equal("http://localhost:1234/mcp", userServer.Endpoint);
        Assert.Equal(McpTransportKind.Http, userServer.Kind);
    }

    [Fact]
    public void Secret_roundtrips_and_clears()
    {
        using var dir = new TempDir();
        var paths = new FakeAppPaths(dir.Path);

        var store = new SecretStore(paths, NullLogger<SecretStore>.Instance);
        Assert.False(store.Has("github_pat"));

        store.Set("github_pat", "ghp_secret_value");
        Assert.True(store.Has("github_pat"));
        Assert.Equal("ghp_secret_value", store.Get("github_pat"));

        // A fresh instance reads it back from disk (DPAPI-decrypted on Windows).
        var reloaded = new SecretStore(paths, NullLogger<SecretStore>.Instance);
        Assert.Equal("ghp_secret_value", reloaded.Get("github_pat"));

        reloaded.Set("github_pat", null);
        Assert.False(reloaded.Has("github_pat"));
    }

    [Fact]
    public void Secret_file_is_not_plaintext_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var dir = new TempDir();
        var paths = new FakeAppPaths(dir.Path);
        new SecretStore(paths, NullLogger<SecretStore>.Instance).Set("github_pat", "ghp_plaintext_probe");

        var raw = File.ReadAllText(Path.Combine(dir.Path, "secrets.json"));
        Assert.DoesNotContain("ghp_plaintext_probe", raw);
    }

    private sealed class FakeAppPaths(string dir) : IAppPaths
    {
        public string SettingsDirectory { get; } = dir;
        public string DataDirectory { get; } = dir;
        public string DownloadsDirectory => Path.Combine(DataDirectory, "downloads");
        public string DefaultServersDirectory => Path.Combine(DataDirectory, "servers");
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcphub-settings-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
