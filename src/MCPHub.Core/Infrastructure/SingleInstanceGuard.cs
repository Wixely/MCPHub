using System.Text;

namespace MCPHub.Core.Infrastructure;

/// <summary>
/// Machine-wide, per-user guard that stops a second MCPHub from starting.
/// <para>
/// A second instance is not a harmless duplicate window: MCPHub binds a fixed proxy port, starts
/// every auto-start service on its own fixed port, and writes to a shared servers folder. Two
/// instances fight over all three, and the loser's failures look like unrelated server faults.
/// </para>
/// <para>
/// Ownership is an exclusive OS lock held on a file for the life of the process. The lock is the
/// mechanism, not the file's contents: the kernel drops it however the process ends, including a
/// kill or a power loss, so there is no stale lock to detect and no PID to second-guess.
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>Name of the lock file within the per-user data directory.</summary>
    public const string LockFileName = "mcphub.lock";

    /// <summary>Switch that lets a caller start an additional instance deliberately.</summary>
    public const string OverrideSwitch = "--allow-multiple-instances";

    private FileStream? _stream;

    private SingleInstanceGuard(FileStream stream) => _stream = stream;

    /// <summary>Where the lock lives: alongside MCPHub's other per-user data.</summary>
    public static string DefaultLockFilePath(IAppPaths paths) =>
        Path.Combine(paths.DataDirectory, LockFileName);

    /// <summary>
    /// True when the command line asks to bypass the guard. Matched case-insensitively and
    /// ignoring surrounding whitespace, since this is typed by hand into shortcuts and shells.
    /// </summary>
    public static bool IsOverrideRequested(IEnumerable<string?>? args) =>
        args is not null &&
        args.Any(a => string.Equals(a?.Trim(), OverrideSwitch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Takes ownership for this process, or reports that another instance already holds it.
    /// The returned guard must be kept alive for as long as the application runs.
    /// </summary>
    /// <returns><see langword="true"/> when this process now owns the lock.</returns>
    public static bool TryAcquire(string lockFilePath, out SingleInstanceGuard? guard)
    {
        guard = null;

        var directory = Path.GetDirectoryName(lockFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere to put the lock. Refusing here would make MCPHub unstartable on a
                // machine where the data directory is unwritable, which is the worse failure.
                return false;
            }
        }

        FileStream stream;
        try
        {
            // FileShare.None is the whole mechanism: an exclusive open on Windows, flock(LOCK_EX)
            // on Unix. Anyone else asking for the same file is refused until this handle closes.
            stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        StampHolder(stream);
        guard = new SingleInstanceGuard(stream);
        return true;
    }

    /// <summary>Releases the lock, letting the next MCPHub start.</summary>
    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Dispose();
        }
        catch (IOException)
        {
            // The handle is going away with the process regardless.
        }
    }

    /// <summary>
    /// Records who holds the lock. Purely diagnostic - nothing reads it back to make a decision,
    /// so a failure to write is not a reason to refuse startup.
    /// </summary>
    private static void StampHolder(FileStream stream)
    {
        try
        {
            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId} machine={Environment.MachineName} startedUtc={DateTimeOffset.UtcNow:O}{Environment.NewLine}"));
            stream.Flush();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
