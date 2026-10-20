using System.Diagnostics;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Updates;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// The updater mode a downloaded build runs in: wait for the old build to exit, install itself, start the
/// installed copy, and put the old one back if anything fails. Plain files and a recorded launcher stand in
/// for the processes; the real thing is proven by running published builds.
/// </summary>
public sealed class HubUpdateApplierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcphub-applier-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;
    private readonly string _source;
    private readonly List<string> _log = [];
    private readonly List<(string Path, IReadOnlyList<string> Args)> _launches = [];

    public HubUpdateApplierTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "install"));
        Directory.CreateDirectory(Path.Combine(_dir, "downloads", "hub-update-1"));
        _target = Path.Combine(_dir, "install", "MCPHub.exe");
        _source = Path.Combine(_dir, "downloads", "hub-update-1", "MCPHub.exe");
        File.WriteAllText(_target, "OLD");
        File.WriteAllText(_source, "NEW");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private HubUpdater.ReplacementLauncher Launcher(Func<string, string?>? answer = null) => (path, args) =>
    {
        _launches.Add((path, args));
        return answer?.Invoke(File.Exists(path) ? File.ReadAllText(path) : "(missing)");
    };

    private int Run(HubUpdateApplier.Plan plan, HubUpdater.ReplacementLauncher launcher, TimeSpan? wait = null)
        => HubUpdateApplier.Run(plan, launcher, _log.Add, wait ?? TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500));

    [Fact]
    public void Installs_itself_and_starts_the_installed_copy_saying_what_it_replaced()
    {
        var code = Run(new(_target, _source, PredecessorPid: int.MaxValue - 1, UpdatedFrom: "0.14.0"), Launcher());

        Assert.Equal(0, code);
        Assert.Equal("NEW", File.ReadAllText(_target));
        Assert.Equal("OLD", File.ReadAllText(HubExecutableSwap.BackupPathFor(_target)));
        var launch = Assert.Single(_launches);
        Assert.Equal(_target, launch.Path);
        Assert.Equal([InstanceRestart.UpdatedFromSwitch, "0.14.0"], launch.Args);
    }

    [Fact]
    public void Does_nothing_while_the_old_build_is_still_running()
    {
        // This test process stands in for an old build that never finishes shutting down.
        var code = Run(new(_target, _source, Environment.ProcessId, "0.14.0"), Launcher(), wait: TimeSpan.FromMilliseconds(300));

        Assert.Equal(2, code);
        Assert.Equal("OLD", File.ReadAllText(_target));
        Assert.False(File.Exists(HubExecutableSwap.BackupPathFor(_target)));
        Assert.Empty(_launches);
        Assert.Contains(_log, l => l.Contains("did not exit"));
    }

    [Fact]
    public void Puts_the_old_build_back_and_starts_it_when_the_new_one_will_not_start()
    {
        // The first launch (the new build) fails; the second (the old build, restored) succeeds.
        var code = Run(new(_target, _source, null, "0.14.0"), Launcher(content => content == "NEW" ? "it crashed" : null));

        Assert.Equal(4, code);
        Assert.Equal("OLD", File.ReadAllText(_target));
        Assert.Equal(2, _launches.Count);
        var restart = _launches[1];
        Assert.Equal(HubUpdateApplier.UpdateFailedSwitch, restart.Args[0]);
        Assert.Contains("it crashed", restart.Args[1]);
        Assert.Equal(restart.Args[1], HubUpdateApplier.ParseUpdateFailure(restart.Args));
    }

    [Fact]
    public void Starts_the_old_build_with_the_reason_when_the_new_one_cannot_be_installed()
    {
        File.Delete(_source); // stands in for anything that stops the copy

        var code = Run(new(_target, _source, null, "0.14.0"), Launcher());

        Assert.Equal(3, code);
        Assert.Equal("OLD", File.ReadAllText(_target));
        var restart = Assert.Single(_launches);
        Assert.Equal(_target, restart.Path);
        Assert.Equal(HubUpdateApplier.UpdateFailedSwitch, restart.Args[0]);
    }

    [Fact]
    public async Task Cleanup_removes_the_backup_and_every_download_folder()
    {
        Run(new(_target, _source, null, "0.14.0"), Launcher());
        var unrelated = Directory.CreateDirectory(Path.Combine(_dir, "downloads", "SomeServer-staging"));

        await HubUpdateApplier.CleanupAsync(_target, Path.Combine(_dir, "downloads"), TimeSpan.FromSeconds(2));

        Assert.False(File.Exists(HubExecutableSwap.BackupPathFor(_target)));
        Assert.False(Directory.Exists(Path.Combine(_dir, "downloads", "hub-update-1")));
        Assert.True(unrelated.Exists, "only the hub's own download folders are removed");
        Assert.Equal("NEW", File.ReadAllText(_target));
    }

    [Fact]
    public void The_command_line_contract_parses_from_anywhere_and_ignores_switch_shaped_values()
    {
        string[] args = [InstanceRestart.WaitForExitSwitch, "42", HubUpdateApplier.ApplyUpdateSwitch, "/opt/mcphub/MCPHub", InstanceRestart.UpdatedFromSwitch, "0.14.0"];

        Assert.Equal("/opt/mcphub/MCPHub", HubUpdateApplier.ParseApplyTarget(args));
        Assert.Equal(42, InstanceRestart.ParseWaitForProcessId(args));
        Assert.Equal("0.14.0", InstanceRestart.ParseUpdatedFromVersion(args));
        Assert.Null(HubUpdateApplier.ParseApplyTarget(["--other"]));
        Assert.Null(HubUpdateApplier.ParseApplyTarget([HubUpdateApplier.ApplyUpdateSwitch, "--restart-after"]));
        Assert.Null(HubUpdateApplier.ParseUpdateFailure(null));
    }

    [Fact]
    public void The_command_line_entry_refuses_without_a_target()
    {
        var log = Path.Combine(_dir, "update.log");

        Assert.Equal(1, HubUpdateApplier.RunFromCommandLine([HubUpdateApplier.ApplyUpdateSwitch], log));
        Assert.Contains("nothing done", File.ReadAllText(log));
        Assert.Equal("OLD", File.ReadAllText(_target));
    }
}
