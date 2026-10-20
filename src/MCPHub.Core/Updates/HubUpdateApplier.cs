using System.Globalization;
using MCPHub.Core.Infrastructure;

namespace MCPHub.Core.Updates;

/// <summary>
/// The second half of a self-update, run by the <em>downloaded</em> build before it does anything else.
///
/// <para>The running build cannot replace its own file: a .NET single-file app keeps reading its bundle by
/// path, so it crashes the moment it loads code from a file that has changed under it. So the running build
/// only downloads and verifies the update, starts that download as
/// <c>MCPHub --apply-update &lt;installed path&gt; --restart-after &lt;its pid&gt; --updated-from &lt;its version&gt;</c>,
/// and shuts down as usual. This class, in the download, waits for it to exit, copies itself over the
/// installed file, starts the installed file, and exits. If the copy or the start fails, the previous build
/// is put back and started instead, told why with <c>--update-failed</c>.</para>
///
/// <para>The command line is a contract between releases: a build running this code is started by the build
/// before it. Add to it; never change what an existing switch means.</para>
/// </summary>
public static class HubUpdateApplier
{
    /// <summary>Starts a build in updater mode; carries the installed executable to replace.</summary>
    public const string ApplyUpdateSwitch = "--apply-update";

    /// <summary>Carries why an update was rolled back, to the previous build started in its place.</summary>
    public const string UpdateFailedSwitch = "--update-failed";

    /// <summary>
    /// How long the updater waits for the old build to finish shutting down — which stops every managed
    /// server, so it can take a while. Past this it installs nothing: replacing a build that is still
    /// running is exactly what this design exists to avoid.
    /// </summary>
    public static readonly TimeSpan DefaultPredecessorTimeout = TimeSpan.FromMinutes(2);

    /// <summary>What the updater was asked to do.</summary>
    /// <param name="Target">The installed executable to replace.</param>
    /// <param name="Source">The new build: the updater's own file.</param>
    /// <param name="PredecessorPid">The build being replaced, which must exit first.</param>
    /// <param name="UpdatedFrom">The version being replaced, passed on to the new build.</param>
    public sealed record Plan(string Target, string Source, int? PredecessorPid, string? UpdatedFrom);

    /// <summary>The installed executable in <c>--apply-update &lt;path&gt;</c>, or <see langword="null"/> for any other launch.</summary>
    public static string? ParseApplyTarget(IReadOnlyList<string?>? args) => ValueAfter(args, ApplyUpdateSwitch);

    /// <summary>The reason in <c>--update-failed &lt;reason&gt;</c>, or <see langword="null"/>.</summary>
    public static string? ParseUpdateFailure(IReadOnlyList<string?>? args) => ValueAfter(args, UpdateFailedSwitch);

    /// <summary>
    /// Runs the updater for this process, logging to <paramref name="logPath"/>, and returns the exit code.
    /// Called from <c>Program.Main</c> before the single-instance guard and before any UI exists.
    /// </summary>
    public static int RunFromCommandLine(IReadOnlyList<string> args, string logPath)
    {
        var log = FileLog(logPath);
        var target = ParseApplyTarget(args);
        var source = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source))
        {
            log("Updater started without a target or without knowing its own path; nothing done.");
            return 1;
        }

        var plan = new Plan(
            Path.GetFullPath(target),
            source,
            InstanceRestart.ParseWaitForProcessId(args),
            InstanceRestart.ParseUpdatedFromVersion(args));

        return Run(plan, (path, arguments) => InstanceRestart.TryLaunchReplacement(path, extraArguments: arguments), log);
    }

    /// <summary>The updater itself, with its process launcher and log supplied, so every path can be tested on plain files.</summary>
    /// <returns>0 when the new build was installed and started; non-zero otherwise, with the reason logged.</returns>
    public static int Run(
        Plan plan,
        HubUpdater.ReplacementLauncher launch,
        Action<string> log,
        TimeSpan? predecessorTimeout = null,
        TimeSpan? swapPatience = null)
    {
        log($"Updating {plan.Target} from {plan.Source} (replacing {plan.UpdatedFrom ?? "an unknown version"}).");

        if (plan.PredecessorPid is { } pid && !InstanceRestart.WaitForProcessExit(pid, predecessorTimeout ?? DefaultPredecessorTimeout))
        {
            // Installing now would replace a running build — the crash this updater exists to avoid. The old
            // build is still there and still running, so leaving everything as it is loses nothing.
            log($"The previous MCPHub (pid {pid.ToString(CultureInfo.InvariantCulture)}) did not exit; nothing was installed.");
            return 2;
        }

        try
        {
            HubExecutableSwap.Install(plan.Target, plan.Source, swapPatience);
            log("Installed the new build.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("Could not install the new build: " + ex.Message);
            RestorePrevious(plan, launch, log,
                $"MCPHub could not install the update: {ex.Message} The previous version is still installed.");
            return 3;
        }

        var arguments = plan.UpdatedFrom is null ? Array.Empty<string>() : new[] { InstanceRestart.UpdatedFromSwitch, plan.UpdatedFrom };
        var failure = launch(plan.Target, arguments);
        if (failure is null)
        {
            log("Started the updated MCPHub.");
            return 0;
        }

        log("Could not start the updated MCPHub: " + failure);
        RestorePrevious(plan, launch, log,
            $"The updated MCPHub could not be started ({failure}). The previous version was put back.");
        return 4;
    }

    /// <summary>
    /// Removes what an update leaves behind once the new build is running: the parked previous build beside
    /// the executable, and the download folders under <paramref name="downloadsDirectory"/> — one of which
    /// the updater was running from a moment ago, hence the patience.
    /// </summary>
    public static async Task CleanupAsync(string? executablePath, string downloadsDirectory, TimeSpan? patience = null)
    {
        await HubExecutableSwap.TryRemoveBackupAsync(executablePath, patience);

        if (!Directory.Exists(downloadsDirectory))
            return;

        var deadline = DateTimeOffset.UtcNow + (patience ?? HubExecutableSwap.DefaultCleanupPatience);
        foreach (var folder in Directory.GetDirectories(downloadsDirectory, HubUpdater.StagingFolderPrefix + "*"))
        {
            while (Directory.Exists(folder))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                        break; // the next start tries again
                    await Task.Delay(250);
                }
            }
        }
    }

    private static void RestorePrevious(Plan plan, HubUpdater.ReplacementLauncher launch, Action<string> log, string reason)
    {
        try
        {
            HubExecutableSwap.Undo(plan.Target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Could not restore the previous build: {ex.Message} It is at {HubExecutableSwap.BackupPathFor(plan.Target)}.");
            return;
        }

        if (!File.Exists(plan.Target))
        {
            log("There is no previous build to start.");
            return;
        }

        var again = launch(plan.Target, [UpdateFailedSwitch, reason]);
        log(again is null ? "Started the previous MCPHub again." : "Could not start the previous MCPHub either: " + again);
    }

    private static Action<string> FileLog(string path) => line =>
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [pid {Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log nobody can write is not a reason to abandon an update half done.
        }
    };

    private static string? ValueAfter(IReadOnlyList<string?>? args, string name)
    {
        if (args is null)
            return null;

        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i]?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = args[i + 1]?.Trim();
            return string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal) ? null : value;
        }

        return null;
    }
}
