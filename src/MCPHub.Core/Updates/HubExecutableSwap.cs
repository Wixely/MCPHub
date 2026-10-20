namespace MCPHub.Core.Updates;

/// <summary>
/// Puts a new MCPHub executable in place of an old one that is <em>not running</em>. The new file is copied
/// in beside the old under a temporary name, the old one is renamed aside as a backup, and the new one takes
/// its name; any failure puts the old one back.
///
/// <para>The old build must have exited first. A .NET single-file app keeps reading its own bundle by path
/// while it runs — whenever it loads an assembly it has not needed yet — so a running build whose file is
/// replaced, even by a rename the OS allows, crashes the next time it loads code. That is why the swap is
/// done by <see cref="HubUpdateApplier"/>, from the downloaded build, after the old one has shut down.</para>
/// </summary>
public static class HubExecutableSwap
{
    /// <summary>Appended to the executable's name while the previous build waits to be cleaned up.</summary>
    public const string BackupSuffix = ".old";

    /// <summary>Appended to the executable's name while the new build is copied in, before it takes the name.</summary>
    public const string IncomingSuffix = ".new";

    /// <summary>How long the swap keeps retrying a file something still holds — a process a moment after it exited, or a virus scanner.</summary>
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(15);

    /// <summary>How long the next start keeps trying to remove the previous build.</summary>
    public static readonly TimeSpan DefaultCleanupPatience = TimeSpan.FromSeconds(10);

    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>Where the previous build is parked: <c>MCPHub.exe.old</c> beside <c>MCPHub.exe</c>.</summary>
    public static string BackupPathFor(string executablePath) => executablePath + BackupSuffix;

    /// <summary>Where the new build is copied before it takes the executable's name.</summary>
    public static string IncomingPathFor(string executablePath) => executablePath + IncomingSuffix;

    /// <summary>
    /// Replaces <paramref name="targetExecutable"/>, which must not be running, with a copy of
    /// <paramref name="sourceExecutable"/>. The source is copied rather than moved, because it is normally the
    /// running updater itself. Copying to a temporary name in the target's own folder first means the final
    /// step is a rename within one folder, never a copy that could be left half-written.
    /// </summary>
    /// <exception cref="FileNotFoundException">Either file is missing; nothing has been touched.</exception>
    /// <exception cref="IOException">A file could not be copied or renamed; the old build is back in place.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder refuses the change; the old build is back in place.</exception>
    public static void Install(string targetExecutable, string sourceExecutable, TimeSpan? patience = null)
    {
        if (!File.Exists(sourceExecutable))
            throw new FileNotFoundException("The new build is missing.", sourceExecutable);
        if (!File.Exists(targetExecutable))
            throw new FileNotFoundException("The executable to replace is missing.", targetExecutable);

        var incoming = IncomingPathFor(targetExecutable);
        var backup = BackupPathFor(targetExecutable);
        var wait = patience ?? DefaultPatience;

        File.Copy(sourceExecutable, incoming, overwrite: true);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(incoming, ExecutableMode);

            Retry(wait, () =>
            {
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Move(targetExecutable, backup);
            });
        }
        catch
        {
            TryDelete(incoming);
            throw;
        }

        try
        {
            File.Move(incoming, targetExecutable);
        }
        catch
        {
            TryDelete(incoming);
            Undo(targetExecutable);
            throw;
        }
    }

    /// <summary>
    /// Reverses an <see cref="Install"/>: removes whatever sits under the target's name and restores the parked
    /// original. Used when the new build will not start, so the user is never left with a build that has not
    /// been proven to launch. A missing backup means there is nothing to undo.
    /// </summary>
    public static void Undo(string targetExecutable)
    {
        var backup = BackupPathFor(targetExecutable);
        if (!File.Exists(backup))
            return;

        if (File.Exists(targetExecutable))
            File.Delete(targetExecutable);
        File.Move(backup, targetExecutable);
    }

    /// <summary>
    /// Removes what an earlier update left beside the executable: the parked previous build, and a
    /// half-copied new one from an install that was interrupted. Retried for a while, since the updater that
    /// started this process may still be exiting. Returns whether both are gone.
    /// </summary>
    public static async Task<bool> TryRemoveBackupAsync(string? executablePath, TimeSpan? patience = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return true;

        var wait = patience ?? DefaultCleanupPatience;
        var backupGone = await TryDeleteAsync(BackupPathFor(executablePath), wait, cancellationToken);
        var incomingGone = await TryDeleteAsync(IncomingPathFor(executablePath), wait, cancellationToken);
        return backupGone && incomingGone;
    }

    /// <summary>Marks a file executable for everyone, as a published build is. A no-op on Windows.</summary>
    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, ExecutableMode);
    }

    private static void Retry(TimeSpan patience, Action action)
    {
        var deadline = DateTimeOffset.UtcNow + patience;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not FileNotFoundException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw;
                Thread.Sleep(250);
            }
        }
    }

    private static async Task<bool> TryDeleteAsync(string path, TimeSpan patience, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + patience;
        while (true)
        {
            if (!File.Exists(path))
                return true;

            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    return false;
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
