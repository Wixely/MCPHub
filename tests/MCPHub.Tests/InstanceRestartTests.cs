using System.Diagnostics;
using MCPHub.Core.Infrastructure;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// The argument handling and wait that let a replacement MCPHub take the single-instance lock only after its
/// predecessor has released it.
/// </summary>
public sealed class InstanceRestartTests
{
    [Fact]
    public void The_switch_is_recognised_wherever_it_appears()
    {
        Assert.Equal(4321, InstanceRestart.ParseWaitForProcessId([InstanceRestart.WaitForExitSwitch, "4321"]));
        Assert.Equal(4321, InstanceRestart.ParseWaitForProcessId(["--other", InstanceRestart.WaitForExitSwitch, "4321"]));
        Assert.Equal(4321, InstanceRestart.ParseWaitForProcessId(["  --RESTART-AFTER ", " 4321 "]));
    }

    [Fact]
    public void An_ordinary_launch_has_no_predecessor_to_wait_for()
    {
        Assert.Null(InstanceRestart.ParseWaitForProcessId(null));
        Assert.Null(InstanceRestart.ParseWaitForProcessId([]));
        Assert.Null(InstanceRestart.ParseWaitForProcessId(["--allow-multiple-instances"]));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-7")]
    [InlineData("99999999999999999999")]
    public void A_nonsensical_pid_is_ignored_rather_than_blocking_startup(string value)
    {
        Assert.Null(InstanceRestart.ParseWaitForProcessId([InstanceRestart.WaitForExitSwitch, value]));
    }

    [Fact]
    public void A_switch_with_nothing_after_it_is_ignored()
    {
        Assert.Null(InstanceRestart.ParseWaitForProcessId([InstanceRestart.WaitForExitSwitch]));
    }

    [Fact]
    public void Waiting_on_a_process_that_is_already_gone_returns_immediately()
    {
        // A pid nothing owns reads as "already exited", which is the only thing the caller needs to know:
        // there is nothing left holding the lock.
        var started = DateTimeOffset.UtcNow;
        Assert.True(InstanceRestart.WaitForProcessExit(int.MaxValue - 1, TimeSpan.FromSeconds(30)));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Waiting_blocks_until_the_predecessor_actually_exits()
    {
        using var predecessor = StartSleeper();
        try
        {
            Assert.False(InstanceRestart.WaitForProcessExit(predecessor.Id, TimeSpan.FromMilliseconds(300)));

            predecessor.Kill();
            Assert.True(InstanceRestart.WaitForProcessExit(predecessor.Id, TimeSpan.FromSeconds(20)));
        }
        finally
        {
            if (!predecessor.HasExited) predecessor.Kill();
        }
    }

    [Fact]
    public void A_replacement_is_not_launched_from_a_path_that_does_not_exist()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "MCPHub.exe");

        // Reported rather than thrown: the caller shuts MCPHub down only when this returns null, so a bad
        // path must leave the running instance alone and say why.
        Assert.Contains("could not find its executable", InstanceRestart.TryLaunchReplacement(missing));
    }

    [Fact]
    public void Running_under_dotnet_is_recognised_so_restart_refuses_instead_of_closing_for_good()
    {
        // dotnet run / dotnet MCPHub.dll: Environment.ProcessPath is the host, and re-launching it with
        // MCPHub's arguments would start dotnet, fail, and leave nothing behind once MCPHub had shut down.
        Assert.True(InstanceRestart.IsLaunchedByAHost(@"C:\Program Files\dotnet\dotnet.exe", "MCPHub"));
        Assert.True(InstanceRestart.IsLaunchedByAHost("/usr/bin/dotnet", "MCPHub"));

        // A published build runs from its own apphost, whatever the extension or casing.
        Assert.False(InstanceRestart.IsLaunchedByAHost(@"C:\sbin\MCPHub\MCPHub.exe", "MCPHub"));
        Assert.False(InstanceRestart.IsLaunchedByAHost("/opt/mcphub/mcphub", "MCPHub"));
    }

    [Fact]
    public void An_unknown_executable_or_assembly_name_does_not_block_a_restart_on_a_guess()
    {
        Assert.False(InstanceRestart.IsLaunchedByAHost("", "MCPHub"));
        Assert.False(InstanceRestart.IsLaunchedByAHost(@"C:\somewhere\MCPHub.exe", ""));
    }

    [Fact]
    public void A_host_launch_is_refused_with_a_message_naming_the_host()
    {
        // The real executable exists, so this reaches the host check rather than the missing-file one.
        var host = Environment.ProcessPath!;

        var failure = InstanceRestart.TryLaunchReplacement(host, entryAssemblyName: "SomethingElse");

        Assert.NotNull(failure);
        Assert.Contains(Path.GetFileName(host), failure);
        Assert.Contains("would start the host instead of MCPHub", failure);
    }

    /// <summary>
    /// A child that stays alive long enough to be waited on. <c>ping</c> rather than <c>timeout</c> on
    /// Windows: <c>timeout</c> exits at once when stdin is not a console, which it is not under a test run.
    /// </summary>
    private static Process StartSleeper()
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping.exe", "-n 31 127.0.0.1")
            : new ProcessStartInfo("sleep", "30");
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        return Process.Start(info)!;
    }
}
