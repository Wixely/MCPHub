using MCPHub.Processes;
using Xunit;

namespace MCPHub.Tests;

public class ProcessHostTests
{
    [Fact]
    public async Task Missing_rooted_executable_faults_without_spawning()
    {
        await using var host = new ProcessHost();
        var states = new List<ProcessStateChange>();
        var lines = new List<ProcessOutputLine>();
        host.StateChanged += states.Add;
        host.OutputReceived += lines.Add;

        await host.StartAsync(new ProcessSpec
        {
            Name = "ghost",
            ExecutablePath = Path.Combine(Path.GetTempPath(), "definitely-not-here.exe"),
        });

        Assert.False(host.IsRunning("ghost"));
        var change = Assert.Single(states);
        Assert.Equal(ProcessRunState.Faulted, change.State);
        Assert.Contains(lines, l => l.Stream == ProcessOutputStream.Info && l.Text.StartsWith("Executable not found:"));
    }

    [Fact]
    public async Task Captures_output_and_reports_unexpected_exit_as_faulted()
    {
        await using var host = new ProcessHost();
        var states = new List<ProcessStateChange>();
        var lines = new List<ProcessOutputLine>();
        var exited = new TaskCompletionSource();
        host.StateChanged += change =>
        {
            lock (states) states.Add(change);
            if (change.State is ProcessRunState.Faulted or ProcessRunState.Stopped)
                exited.TrySetResult();
        };
        host.OutputReceived += line => { lock (lines) lines.Add(line); };

        // A short-lived process that exits on its own is an "unexpected" exit (no stop requested).
        await host.StartAsync(new ProcessSpec
        {
            Name = "version-probe",
            ExecutablePath = "dotnet",
            Arguments = ["--version"],
        });

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));

        lock (states)
        {
            Assert.Equal(ProcessRunState.Starting, states.First().State);
            Assert.Equal(ProcessRunState.Faulted, states.Last().State);
        }
        lock (lines)
        {
            Assert.Contains(lines, l => l.Stream == ProcessOutputStream.Info && l.Text.StartsWith("Started (pid "));
            Assert.Contains(lines, l => l.Stream == ProcessOutputStream.Stdout && l.Text.Trim().Length > 0);
            Assert.Contains(lines, l => l.Stream == ProcessOutputStream.Info && l.Text.StartsWith("Exited unexpectedly"));
        }
        Assert.False(host.IsRunning("version-probe"));
    }

    [Fact]
    public async Task Stop_of_unknown_process_reports_stopped()
    {
        await using var host = new ProcessHost();
        var states = new List<ProcessStateChange>();
        host.StateChanged += states.Add;

        await host.StopAsync("never-started");

        var change = Assert.Single(states);
        Assert.Equal(ProcessRunState.Stopped, change.State);
        Assert.Null(change.ProcessId);
    }
}
