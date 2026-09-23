using MCPHub.Core.Infrastructure;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// Handing a refused second launch over to the running instance. The transport is exercised for real: a
/// listener on an actual named pipe, and a client that is a separate object with no knowledge of it.
/// </summary>
public sealed class InstanceActivationTests
{
    /// <summary>Well under the production timeout, so a broken channel fails the test rather than stalling it.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_second_launch_raises_the_running_instance()
    {
        var channel = NewChannelName();
        var shown = new ManualResetEventSlim(false);
        using var listener = InstanceActivation.Listener.TryStart(channel, shown.Set);
        Assert.NotNull(listener);

        Assert.True(InstanceActivation.TryRequestActivation(channel, Timeout));
        Assert.True(shown.IsSet);
    }

    [Fact]
    public void The_channel_keeps_answering_launch_after_launch()
    {
        var channel = NewChannelName();
        var count = 0;
        using var listener = InstanceActivation.Listener.TryStart(channel, () => Interlocked.Increment(ref count));
        Assert.NotNull(listener);

        for (var i = 0; i < 3; i++)
            Assert.True(InstanceActivation.TryRequestActivation(channel, Timeout));

        Assert.Equal(3, Volatile.Read(ref count));
    }

    [Fact]
    public void With_nothing_listening_the_request_fails_instead_of_hanging()
    {
        // This is the case that must keep the old "already running" message and exit code: an MCPHub too
        // wedged to answer, or one from a build with no activation channel at all.
        var started = DateTimeOffset.UtcNow;
        Assert.False(InstanceActivation.TryRequestActivation(NewChannelName(), TimeSpan.FromMilliseconds(600)));
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void A_disposed_listener_stops_answering_so_a_dead_instance_cannot_look_alive()
    {
        var channel = NewChannelName();
        var listener = InstanceActivation.Listener.TryStart(channel, () => { });
        Assert.NotNull(listener);
        Assert.True(InstanceActivation.TryRequestActivation(channel, Timeout));

        listener.Dispose();

        Assert.False(InstanceActivation.TryRequestActivation(channel, TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public void The_channel_name_follows_the_lock_file_and_is_a_legal_pipe_name()
    {
        var mine = InstanceActivation.ChannelNameFor(Path.Combine(Path.GetTempPath(), "a", "mcphub.lock"));
        var theirs = InstanceActivation.ChannelNameFor(Path.Combine(Path.GetTempPath(), "b", "mcphub.lock"));

        // Separate data directories are separate instances and must not summon each other.
        Assert.NotEqual(mine, theirs);
        // Case and separator spelling of the same path are the same instance.
        Assert.Equal(mine, InstanceActivation.ChannelNameFor(Path.Combine(Path.GetTempPath(), "a", ".", "MCPHUB.LOCK")));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, mine);
        Assert.DoesNotContain('/', mine);
    }

    /// <summary>Unique per test so a parallel run cannot have two listeners fighting for one pipe.</summary>
    private static string NewChannelName() =>
        InstanceActivation.ChannelNameFor(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "mcphub.lock"));
}
