using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Services;
using MCPHub.Core.Services.Github;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Updates;

/// <summary>Where an update is, for the progress bar: the phase, and how far through it when that is known.</summary>
public sealed record HubUpdateProgress(string Phase, double? Fraction);

/// <summary>A downloaded, verified and unpacked build, ready to be swapped in.</summary>
/// <param name="ChecksumVerified">Whether the release carried a checksum file and the download matched it.</param>
public sealed record StagedHubUpdate(string Version, string ExecutablePath, string StagingDirectory, bool ChecksumVerified);

/// <summary>Replaces the running MCPHub with a published release, in two halves so the UI can show each.</summary>
public interface IHubUpdater
{
    /// <summary>The running build, which decides which asset is fetched and whether any can be.</summary>
    HubBuild Build { get; }

    /// <summary>
    /// Downloads the build for this machine, checks its size and (when the release carries one) its SHA-256
    /// checksum, and unpacks it. Nothing of the running installation is touched. Throws when the running
    /// build cannot be updated, the release has no build for it, or the download does not verify; whatever
    /// was downloaded is removed before the exception leaves.
    /// </summary>
    Task<StagedHubUpdate> StageAsync(ReleaseInfo release, IProgress<HubUpdateProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the staged build as the updater (see <see cref="HubUpdateApplier"/>): it waits for this process
    /// to exit, then installs itself and starts the installed copy. The caller shuts MCPHub down next. Nothing
    /// in the install folder is touched here, because the running build's file must not change while it runs.
    /// Returns <see langword="null"/> on success, or why nothing was started.
    /// </summary>
    string? Apply(StagedHubUpdate staged);

    /// <summary>Removes a staged build that will not be applied.</summary>
    void Discard(StagedHubUpdate staged);
}

/// <inheritdoc />
public sealed class HubUpdater : IHubUpdater
{
    /// <summary>Starts the replacement; returns a failure message or <see langword="null"/>.</summary>
    public delegate string? ReplacementLauncher(string executablePath, IReadOnlyList<string> arguments);

    /// <summary>Downloads are unpacked into folders named with this prefix, which a later start cleans up.</summary>
    public const string StagingFolderPrefix = "hub-update-";

    private const long ChecksumFileLimit = 256 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAppPaths _paths;
    private readonly ILogger<HubUpdater> _logger;
    private readonly ReplacementLauncher _launch;

    public HubUpdater(IHttpClientFactory httpClientFactory, IAppPaths paths, ILogger<HubUpdater> logger)
        : this(httpClientFactory, paths, logger, HubBuild.Current, launcher: null)
    {
    }

    /// <summary>For tests: a described build and a launcher that does not start a process.</summary>
    public HubUpdater(IHttpClientFactory httpClientFactory, IAppPaths paths, ILogger<HubUpdater> logger, HubBuild build, ReplacementLauncher? launcher)
    {
        _httpClientFactory = httpClientFactory;
        _paths = paths;
        _logger = logger;
        Build = build;
        _launch = launcher ?? ((path, args) => InstanceRestart.TryLaunchReplacement(path, extraArguments: args));
    }

    /// <inheritdoc />
    public HubBuild Build { get; }

    /// <inheritdoc />
    public async Task<StagedHubUpdate> StageAsync(ReleaseInfo release, IProgress<HubUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Build.SelfUpdateBlocker is { } blocker)
            throw new InvalidOperationException(blocker);

        var asset = HubRelease.AssetFor(release, Build.RuntimeIdentifier)
            ?? throw new InvalidOperationException($"Release {release.Version} has no build for {Build.RuntimeIdentifier}. Only win-x64 and linux-x64 builds are published.");

        var downloads = _paths.EnsureDirectory(_paths.DownloadsDirectory);
        var staging = Path.Combine(downloads, StagingFolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var zipPath = Path.Combine(staging, asset.Name);

        try
        {
            _logger.LogInformation("Downloading MCPHub {Version} ({Asset}).", release.Version, asset.Name);
            var phase = $"Downloading {asset.Name}";
            progress?.Report(new HubUpdateProgress(phase, 0));
            await DownloadAsync(asset.DownloadUrl, zipPath, fraction => progress?.Report(new HubUpdateProgress(phase, fraction)), cancellationToken);

            var length = new FileInfo(zipPath).Length;
            if (asset.Size > 0 && length != asset.Size)
                throw new InvalidDataException($"{asset.Name} is {length:N0} bytes but the release lists {asset.Size:N0}; the download was cut short or altered.");

            progress?.Report(new HubUpdateProgress("Verifying", null));
            var verified = await VerifyChecksumAsync(release, asset, zipPath, cancellationToken);

            progress?.Report(new HubUpdateProgress("Unpacking", null));
            var unpacked = Path.Combine(staging, "unpacked");
            ZipFile.ExtractToDirectory(zipPath, unpacked, overwriteFiles: true);

            var executable = FindExecutable(unpacked)
                ?? throw new FileNotFoundException($"{asset.Name} does not contain {Build.ExecutableFileName}.");
            if (new FileInfo(executable).Length == 0)
                throw new InvalidDataException($"{Path.GetFileName(executable)} inside {asset.Name} is empty.");

            File.Delete(zipPath);
            progress?.Report(new HubUpdateProgress("Ready to install", 1));
            return new StagedHubUpdate(release.Version, executable, staging, verified);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    /// <inheritdoc />
    public string? Apply(StagedHubUpdate staged)
    {
        if (Build.SelfUpdateBlocker is { } blocker)
            return blocker;
        var target = Build.ExecutablePath!; // a build with no path is blocked above
        if (!File.Exists(staged.ExecutablePath))
            return "The downloaded build is no longer where it was unpacked. Download it again.";

        // Refused up front rather than by the updater after this process has gone: a folder this user cannot
        // write to would otherwise cost them a shutdown and a restart for nothing.
        if (CannotWriteBeside(target) is { } unwritable)
            return unwritable;

        try
        {
            HubExecutableSwap.MakeExecutable(staged.ExecutablePath); // a zip does not always carry the bit
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The downloaded build could not be made executable: " + ex.Message;
        }

        var failure = _launch(staged.ExecutablePath,
            [HubUpdateApplier.ApplyUpdateSwitch, target, InstanceRestart.UpdatedFromSwitch, HubRelease.CurrentVersion]);
        if (failure is not null)
        {
            _logger.LogError("The downloaded MCPHub could not be started to install itself: {Failure}", failure);
            TryDeleteDirectory(staged.StagingDirectory);
            return $"The downloaded build could not be started to install itself: {failure} Nothing has been changed.";
        }

        // The staging folder stays: the updater is running from it. The new build removes it once it is up.
        _logger.LogInformation("MCPHub {Version} will install itself once this process exits.", staged.Version);
        return null;
    }

    /// <inheritdoc />
    public void Discard(StagedHubUpdate staged) => TryDeleteDirectory(staged.StagingDirectory);

    /// <summary>Whether a file can be created beside the executable, which the updater will need; the reason if not.</summary>
    private static string? CannotWriteBeside(string target)
    {
        var folder = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(folder))
            return null;

        var probe = Path.Combine(folder, ".mcphub-update-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"MCPHub cannot write to {folder}, so it cannot install an update there ({ex.Message}). " +
                   "Run it from a folder you can write to, or download the release and replace the file by hand.";
        }
    }

    private async Task DownloadAsync(string url, string destinationPath, Action<double> report, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(DownloadService.HttpClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (total is > 0)
                report(Math.Min(1.0, (double)received / total.Value));
        }
    }

    /// <summary>
    /// Checks the zip against the release's <c>SHA256SUMS</c>. A release without one is accepted, since
    /// releases before the file existed have none; a release with one that does not list this asset, or
    /// lists a different digest, is refused — a wrong file is worse than no file.
    /// </summary>
    private async Task<bool> VerifyChecksumAsync(ReleaseInfo release, ReleaseAsset asset, string zipPath, CancellationToken cancellationToken)
    {
        var sums = HubRelease.ChecksumAssetFor(release);
        if (sums is null)
        {
            _logger.LogInformation("Release {Version} carries no {File}; the download is checked by size only.", release.Version, HubRelease.ChecksumAssetName);
            return false;
        }

        var http = _httpClientFactory.CreateClient(DownloadService.HttpClientName);
        using var response = await http.GetAsync(sums.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > ChecksumFileLimit)
            throw new InvalidDataException($"{HubRelease.ChecksumAssetName} is implausibly large.");

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var expected = ReleaseChecksums.Parse(text).DigestFor(asset.Name)
            ?? throw new InvalidDataException($"{HubRelease.ChecksumAssetName} in release {release.Version} does not list {asset.Name}, so the download cannot be verified.");

        string actual;
        await using (var stream = File.OpenRead(zipPath))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{asset.Name} does not match the checksum the release publishes for it. The download was corrupted or altered, so it has been discarded.");

        return true;
    }

    private string? FindExecutable(string root)
    {
        foreach (var name in new[] { Build.ExecutableFileName, Build.DefaultExecutableFileName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
                return found;
        }

        return null;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove {Path}.", path);
        }
    }
}
