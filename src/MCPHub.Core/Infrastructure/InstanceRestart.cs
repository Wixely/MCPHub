using System.Diagnostics;
using System.Globalization;
using DiagProcess = System.Diagnostics.Process;

namespace MCPHub.Core.Infrastructure;

/// <summary>
/// Restarting MCPHub in place. The difficulty is <see cref="SingleInstanceGuard"/>: the replacement cannot
/// take the lock until the outgoing process has let go of it, and the outgoing process cannot wait for the
/// replacement without deadlocking on itself. So the replacement is started first and waits, off its own
/// back, for the process that spawned it to exit — no launcher script, no detached shell, and nothing left
/// behind if the shutdown never completes.
/// </summary>
public static class InstanceRestart
{
    /// <summary>Command-line switch carrying the process id the new instance must outlive.</summary>
    public const string WaitForExitSwitch = "--restart-after";

    /// <summary>
    /// How long a replacement waits for its predecessor. Generous, because shutdown stops every managed
    /// server; on expiry it starts anyway and the lock decides, which at worst reports "already running".
    /// </summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The process id in <c>--restart-after &lt;pid&gt;</c>, or <see langword="null"/> when this is an
    /// ordinary launch. A malformed or nonsensical value is ignored rather than refused: it would be a poor
    /// reason to prevent MCPHub from starting.
    /// </summary>
    public static int? ParseWaitForProcessId(IReadOnlyList<string?>? args)
    {
        if (args is null)
            return null;

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i]?.Trim(), WaitForExitSwitch, StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(args[i + 1]?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
                ? pid
                : null;
        }

        return null;
    }

    /// <summary>
    /// Blocks until process <paramref name="processId"/> has exited, or <paramref name="timeout"/> elapses.
    /// Returns whether it is now gone. A process that has already exited — or was never ours to see — counts
    /// as gone, since the only thing being waited on is the release of the lock.
    /// </summary>
    public static bool WaitForProcessExit(int processId, TimeSpan? timeout = null)
    {
        try
        {
            using var process = DiagProcess.GetProcessById(processId);
            return process.WaitForExit((int)(timeout ?? DefaultWaitTimeout).TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true; // No such process: it has already exited.
        }
        catch (InvalidOperationException)
        {
            return true; // It exited between being found and being waited on.
        }
    }

    /// <summary>
    /// Starts a replacement MCPHub that waits for this process to exit before claiming the lock. Call it
    /// <em>before</em> shutting down: once the outgoing process is gone there is nobody left to start one.
    /// </summary>
    /// <param name="executablePath">
    /// The program to run; defaults to this process's own executable.
    /// </param>
    /// <param name="entryAssemblyName">
    /// The name the executable is expected to have; defaults to the entry assembly's. See
    /// <see cref="IsLaunchedByAHost"/> for what this is guarding against.
    /// </param>
    /// <returns><see langword="null"/> on success, or a message explaining why nothing was started.</returns>
    public static string? TryLaunchReplacement(string? executablePath = null, string? entryAssemblyName = null)
    {
        var path = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            return "MCPHub could not work out its own executable path, so it cannot restart itself. Close it and start it again.";
        if (!File.Exists(path))
            return $"MCPHub could not find its executable at {path}, so it cannot restart itself. Close it and start it again.";
        if (IsLaunchedByAHost(path, entryAssemblyName))
            return $"MCPHub is running under {Path.GetFileName(path)} rather than from its own executable, so restarting it " +
                   "would start the host instead of MCPHub. Stop and start it the way you launched it.";

        try
        {
            var startInfo = new ProcessStartInfo(path) { UseShellExecute = true };
            startInfo.ArgumentList.Add(WaitForExitSwitch);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            // Carried over explicitly rather than left to the shell: a deployment that is started from its
            // own folder resolves paths against it, and a replacement landing elsewhere would not be a restart.
            startInfo.WorkingDirectory = Environment.CurrentDirectory;

            return DiagProcess.Start(startInfo) is null
                ? "MCPHub could not start a replacement process. Close it and start it again."
                : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "MCPHub could not start a replacement process: " + ex.Message;
        }
    }

    /// <summary>
    /// Whether this process was started through a host launcher rather than from MCPHub's own apphost — the
    /// <c>dotnet run</c> / <c>dotnet MCPHub.dll</c> case, where <see cref="Environment.ProcessPath"/> is
    /// <c>dotnet</c>. Re-launching that path with MCPHub's arguments would start the host, fail, and leave
    /// the user with nothing once the outgoing process had shut down. Refusing is the only safe answer, and
    /// it has to be detected rather than documented: getting it wrong destroys the running app.
    /// </summary>
    public static bool IsLaunchedByAHost(string? executablePath = null, string? entryAssemblyName = null)
    {
        var path = executablePath ?? Environment.ProcessPath;
        var expected = entryAssemblyName ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expected))
            return false; // Nothing to compare against: attempt the launch rather than block on a guess.

        return !string.Equals(Path.GetFileNameWithoutExtension(path), expected, StringComparison.OrdinalIgnoreCase);
    }
}
