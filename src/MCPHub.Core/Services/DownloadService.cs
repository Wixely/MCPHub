using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Logging;
using MCPHub.Core.Models;
using MCPHub.Core.Process;
using MCPHub.Core.Services.Github;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Services;

/// <summary>Downloads a release asset and installs it into the shared servers folder, preserving config.</summary>
public interface IDownloadService
{
    Task InstallAsync(ManagedService service, ReleaseAsset asset, string version,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class DownloadService : IDownloadService
{
    /// <summary>Name of the configured long-timeout download <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "downloads";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigMergeService _configMerge;
    private readonly IInstalledManifestStore _manifestStore;
    private readonly IServiceProcessHost _processHost;
    private readonly ILogStore _logStore;
    private readonly IAppPaths _appPaths;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(
        IHttpClientFactory httpClientFactory,
        IConfigMergeService configMerge,
        IInstalledManifestStore manifestStore,
        IServiceProcessHost processHost,
        ILogStore logStore,
        IAppPaths appPaths,
        ILogger<DownloadService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configMerge = configMerge;
        _manifestStore = manifestStore;
        _processHost = processHost;
        _logStore = logStore;
        _appPaths = appPaths;
        _logger = logger;
    }

    public async Task InstallAsync(ManagedService service, ReleaseAsset asset, string version,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var name = service.Catalog.Name;
        Info(name, $"Installing {name} {version} ({asset.Name})…");

        // Stop a running instance first so its executable isn't locked.
        if (_processHost.IsRunning(name))
        {
            Info(name, "Stopping running instance before update…");
            await _processHost.StopAsync(service, cancellationToken);
            await WaitUntilStoppedAsync(name, TimeSpan.FromSeconds(5));
        }

        var downloads = _appPaths.EnsureDirectory(_appPaths.DownloadsDirectory);
        var zipPath = Path.Combine(downloads, asset.Name);
        var staging = Path.Combine(downloads, "staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            Info(name, $"Downloading {asset.Name}…");
            await DownloadAsync(asset.DownloadUrl, zipPath, progress, cancellationToken);

            Info(name, "Extracting…");
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            var exeName = service.Catalog.ExecutableFileName();
            var extractedExe = Directory.GetFiles(staging, exeName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException($"{exeName} not found inside {asset.Name}.");
            var sourceDir = Path.GetDirectoryName(extractedExe)!;

            var serversFolder = service.InstallFolder;
            Directory.CreateDirectory(serversFolder);
            var configPath = service.ConfigPath;

            // Capture and back up the existing user config before it is overwritten by the copy.
            var existingConfig = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath, cancellationToken) : null;
            if (existingConfig is not null)
            {
                var backup = $"{configPath}.bak-{Timestamp()}";
                File.Copy(configPath, backup, overwrite: true);
                Info(name, $"Backed up existing config → {Path.GetFileName(backup)}");
            }

            Info(name, "Installing files…");
            CopyDirectory(sourceDir, serversFolder);

            // Merge the user's settings back into the freshly-copied default config.
            if (existingConfig is not null && File.Exists(configPath))
            {
                var newDefault = await File.ReadAllTextAsync(configPath, cancellationToken);
                var merged = _configMerge.MergeJson(existingConfig, newDefault);
                await AtomicWriteAsync(configPath, merged, cancellationToken);
                Info(name, "Merged your settings into the new config.");
            }

            MakeExecutable(Path.Combine(serversFolder, exeName));

            await _manifestStore.SetVersionAsync(serversFolder, name, version, cancellationToken);
            service.InstalledVersion = version;
            service.LatestVersion ??= version;
            service.UpdateStatus = UpdateStatus.UpToDate;
            service.Port = ServerConfigReader.ReadPort(configPath) ?? service.Catalog.DefaultPort;

            progress?.Report(1.0);
            Info(name, $"Installed {name} {version}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Info(name, "Install failed: " + ex.Message);
            _logger.LogError(ex, "Install failed for {Service}.", name);
            throw;
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDirectory(staging);
        }
    }

    private async Task DownloadAsync(string url, string destPath, IProgress<double>? progress, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(destPath);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            if (total is > 0)
                progress?.Report(Math.Min(1.0, (double)received / total.Value));
        }
    }

    private async Task WaitUntilStoppedAsync(string name, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (_processHost.IsRunning(name) && DateTimeOffset.Now < deadline)
            await Task.Delay(150);
        await Task.Delay(300); // brief grace for the OS to release the executable handle
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFileWithFallback(file, target);
        }
    }

    private static void CopyFileWithFallback(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException)
        {
            // Target may be momentarily locked (e.g. a just-stopped exe). Rename it aside, then copy.
            if (File.Exists(target))
            {
                try { File.Move(target, $"{target}.old-{Timestamp()}"); }
                catch { /* ignore — fall through to retry */ }
            }
            File.Copy(source, target, overwrite: true);
        }
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }

    private static void MakeExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !File.Exists(path))
            return;

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    private void Info(string service, string text)
        => _logStore.Append(service, new LogLine(DateTimeOffset.Now, LogStream.Info, text));

    private static string Timestamp() => DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
