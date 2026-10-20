using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Services.Github;
using MCPHub.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// Download, verification and swap of a hub update, against a stubbed GitHub: the "release" is a zip and
/// a checksum file served from memory, the "running executable" a file in a temp folder, and the
/// replacement launch a recorded call rather than a process.
/// </summary>
public sealed class HubUpdaterTests : IDisposable
{
    private const string Rid = "test-x64";
    private const string ZipName = "MCPHub-test-x64-v9.9.9.zip";
    private const string ZipUrl = "https://example.invalid/" + ZipName;
    private const string SumsUrl = "https://example.invalid/SHA256SUMS";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcphub-updater-" + Guid.NewGuid().ToString("N"));
    private readonly string _installDir;
    private readonly string _target;
    private readonly byte[] _zip;

    public HubUpdaterTests()
    {
        _installDir = Path.Combine(_dir, "install");
        Directory.CreateDirectory(_installDir);
        _target = Path.Combine(_installDir, "MCPHub.exe");
        File.WriteAllText(_target, "OLD");
        _zip = Zip(("MCPHub.exe", "NEW"), ("README.txt", "notes"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private HubBuild PublishedBuild => new(Rid, _target, IsSingleFilePublish: true, IsLaunchedByAHost: false);

    private string Downloads => Path.Combine(_dir, "downloads");

    private HubUpdater Updater(StubHandler handler, HubBuild? build = null, HubUpdater.ReplacementLauncher? launcher = null)
        => new(new StubFactory(handler), new TestPaths(_dir, Downloads), NullLogger<HubUpdater>.Instance, build ?? PublishedBuild, launcher);

    private static ReleaseInfo Release(long? zipSize, bool withSums) => new(
        "9.9.9", "v9.9.9", null, false,
        withSums
            ? [new ReleaseAsset(ZipName, ZipUrl, zipSize ?? 0), new ReleaseAsset("SHA256SUMS", SumsUrl, 100)]
            : [new ReleaseAsset(ZipName, ZipUrl, zipSize ?? 0)]);

    private StubHandler Serving(string? sumsText) => new(new Dictionary<string, byte[]>
    {
        [ZipUrl] = _zip,
        [SumsUrl] = Encoding.UTF8.GetBytes(sumsText ?? string.Empty),
    });

    private string Sums(string digest) => $"{digest}  {ZipName}\n";

    private string GoodDigest => Convert.ToHexStringLower(SHA256.HashData(_zip));

    [Fact]
    public async Task Stage_downloads_verifies_and_unpacks_the_build_for_this_runtime()
    {
        var phases = new List<string>();
        var progress = new RecordingProgress(p => phases.Add(p.Phase));
        var updater = Updater(Serving(Sums(GoodDigest)));

        var staged = await updater.StageAsync(Release(_zip.Length, withSums: true), progress);

        Assert.Equal("9.9.9", staged.Version);
        Assert.True(staged.ChecksumVerified);
        Assert.Equal("NEW", File.ReadAllText(staged.ExecutablePath));
        Assert.StartsWith(Downloads, staged.StagingDirectory);
        Assert.Empty(Directory.GetFiles(staged.StagingDirectory, "*.zip")); // the archive is not kept around
        Assert.Contains(phases, p => p.StartsWith("Downloading"));
        Assert.Contains("Verifying", phases);
        Assert.Contains("Ready to install", phases);

        updater.Discard(staged);
        Assert.False(Directory.Exists(staged.StagingDirectory));
    }

    [Fact]
    public async Task Stage_refuses_a_download_whose_checksum_does_not_match()
    {
        var updater = Updater(Serving(Sums(new string('0', 64))));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => updater.StageAsync(Release(_zip.Length, withSums: true)));

        Assert.Contains("checksum", ex.Message);
        AssertNothingStaged();
    }

    [Fact]
    public async Task Stage_refuses_when_the_checksum_file_does_not_list_the_asset()
    {
        var updater = Updater(Serving($"{GoodDigest}  MCPHub-other-x64-v9.9.9.zip\n"));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => updater.StageAsync(Release(_zip.Length, withSums: true)));

        Assert.Contains("does not list", ex.Message);
        AssertNothingStaged();
    }

    [Fact]
    public async Task Stage_accepts_a_release_published_without_checksums_but_says_so()
    {
        var updater = Updater(Serving(null));

        var staged = await updater.StageAsync(Release(_zip.Length, withSums: false));

        Assert.False(staged.ChecksumVerified);
        Assert.Equal("NEW", File.ReadAllText(staged.ExecutablePath));
    }

    [Fact]
    public async Task Stage_refuses_a_download_of_the_wrong_size()
    {
        var updater = Updater(Serving(Sums(GoodDigest)));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => updater.StageAsync(Release(_zip.Length + 7, withSums: true)));

        Assert.Contains("bytes", ex.Message);
        AssertNothingStaged();
    }

    [Fact]
    public async Task Stage_refuses_a_build_that_cannot_update_itself_before_touching_the_network()
    {
        var handler = Serving(Sums(GoodDigest));
        var dev = new HubBuild(Rid, _target, IsSingleFilePublish: false, IsLaunchedByAHost: false);
        var updater = Updater(handler, dev);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => updater.StageAsync(Release(_zip.Length, withSums: true)));

        Assert.Equal(dev.SelfUpdateBlocker, ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Stage_refuses_a_release_with_no_build_for_this_runtime()
    {
        var handler = Serving(Sums(GoodDigest));
        var arm = new HubBuild("linux-arm64", _target, true, false);
        var updater = Updater(handler, arm);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => updater.StageAsync(Release(_zip.Length, withSums: true)));

        Assert.Contains("linux-arm64", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Apply_starts_the_download_as_the_updater_and_leaves_the_install_folder_alone()
    {
        string? launchedPath = null;
        IReadOnlyList<string>? launchedArgs = null;
        var updater = Updater(Serving(Sums(GoodDigest)), launcher: (path, args) =>
        {
            launchedPath = path;
            launchedArgs = args;
            return null;
        });
        var staged = await updater.StageAsync(Release(_zip.Length, withSums: true));

        var failure = updater.Apply(staged);

        Assert.Null(failure);
        // The running build's file must not change while it runs: the download installs itself later.
        Assert.Equal("OLD", File.ReadAllText(_target));
        Assert.Equal(["MCPHub.exe"], Directory.GetFiles(_installDir).Select(Path.GetFileName));
        Assert.Equal(staged.ExecutablePath, launchedPath);
        Assert.Equal(
            [HubUpdateApplier.ApplyUpdateSwitch, _target, InstanceRestart.UpdatedFromSwitch, HubRelease.CurrentVersion],
            launchedArgs);
        Assert.True(Directory.Exists(staged.StagingDirectory), "the updater runs from the download, so it stays");
    }

    [Fact]
    public async Task Apply_reports_and_cleans_up_when_the_download_will_not_start()
    {
        var updater = Updater(Serving(Sums(GoodDigest)), launcher: (_, _) => "could not start it");
        var staged = await updater.StageAsync(Release(_zip.Length, withSums: true));

        var failure = updater.Apply(staged);

        Assert.NotNull(failure);
        Assert.Contains("could not start it", failure);
        Assert.Contains("Nothing has been changed", failure);
        Assert.Equal("OLD", File.ReadAllText(_target));
        Assert.False(Directory.Exists(staged.StagingDirectory));
    }

    [Fact]
    public async Task Apply_reports_a_staged_build_that_has_gone_missing()
    {
        var launched = false;
        var updater = Updater(Serving(Sums(GoodDigest)), launcher: (_, _) => { launched = true; return null; });
        var staged = await updater.StageAsync(Release(_zip.Length, withSums: true));
        updater.Discard(staged);

        var failure = updater.Apply(staged);

        Assert.NotNull(failure);
        Assert.False(launched);
        Assert.Equal("OLD", File.ReadAllText(_target));
    }

    private void AssertNothingStaged()
    {
        Assert.True(!Directory.Exists(Downloads) || Directory.GetDirectories(Downloads).Length == 0,
            "a refused download must not leave a staging folder behind");
    }

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return stream.ToArray();
    }

    private sealed class RecordingProgress(Action<HubUpdateProgress> record) : IProgress<HubUpdateProgress>
    {
        public void Report(HubUpdateProgress value) => record(value);
    }

    private sealed class StubHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var url = request.RequestUri!.ToString();
            if (!files.TryGetValue(url, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TestPaths(string root, string downloads) : IAppPaths
    {
        public string SettingsDirectory => root;
        public string DataDirectory => root;
        public string DownloadsDirectory => downloads;
        public string DefaultServersDirectory => Path.Combine(root, "servers");
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
    }
}
