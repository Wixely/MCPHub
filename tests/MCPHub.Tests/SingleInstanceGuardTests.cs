using MCPHub.Core.Infrastructure;
using Xunit;

namespace MCPHub.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void First_acquire_succeeds()
    {
        using var dir = new TempDir();

        Assert.True(SingleInstanceGuard.TryAcquire(LockPath(dir), out var guard));

        using (guard)
        {
            Assert.NotNull(guard);
        }
    }

    [Fact]
    public void Second_acquire_is_refused_while_the_first_is_held()
    {
        using var dir = new TempDir();
        var path = LockPath(dir);

        Assert.True(SingleInstanceGuard.TryAcquire(path, out var first));
        using (first)
        {
            Assert.False(SingleInstanceGuard.TryAcquire(path, out var second));
            Assert.Null(second);
        }
    }

    [Fact]
    public void Releasing_lets_the_next_instance_start()
    {
        using var dir = new TempDir();
        var path = LockPath(dir);

        Assert.True(SingleInstanceGuard.TryAcquire(path, out var first));
        first!.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(path, out var second));
        second!.Dispose();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        using var dir = new TempDir();
        var path = LockPath(dir);

        Assert.True(SingleInstanceGuard.TryAcquire(path, out var guard));
        guard!.Dispose();
        guard.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(path, out var next));
        next!.Dispose();
    }

    [Fact]
    public void Missing_directory_is_created()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "nested", "deeper", SingleInstanceGuard.LockFileName);

        Assert.True(SingleInstanceGuard.TryAcquire(path, out var guard));
        using (guard)
        {
            Assert.True(File.Exists(path));
        }
    }

    [Fact]
    public void Acquire_distinguishes_a_held_lock_from_an_unavailable_one()
    {
        using var dir = new TempDir();
        var path = LockPath(dir);

        Assert.Equal(SingleInstanceOutcome.Acquired, SingleInstanceGuard.Acquire(path, out var first));
        using (first)
        {
            Assert.Equal(SingleInstanceOutcome.AlreadyHeld, SingleInstanceGuard.Acquire(path, out var second));
            Assert.Null(second);
        }
    }

    [Fact]
    public void Unwritable_lock_directory_is_reported_not_mistaken_for_a_running_instance()
    {
        using var dir = new TempDir();

        // A file sitting where the lock's directory should be: CreateDirectory cannot succeed.
        var blocker = Path.Combine(dir.Path, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var path = Path.Combine(blocker, SingleInstanceGuard.LockFileName);

        Assert.Equal(SingleInstanceOutcome.LockUnavailable, SingleInstanceGuard.Acquire(path, out var guard));
        Assert.Null(guard);
        Assert.False(SingleInstanceGuard.TryAcquire(path, out _));
    }

    [Fact]
    public void Holder_is_stamped_into_the_lock_file()
    {
        using var dir = new TempDir();
        var path = LockPath(dir);

        Assert.True(SingleInstanceGuard.TryAcquire(path, out var guard));
        guard!.Dispose();

        Assert.Contains($"pid={Environment.ProcessId}", File.ReadAllText(path));
    }

    [Fact]
    public void A_stale_lock_file_does_not_block_startup()
    {
        using var dir = new TempDir();
        var path = LockPath(dir);

        // What a killed instance leaves behind: the file survives, the OS lock does not.
        File.WriteAllText(path, "pid=999999 machine=gone startedUtc=2020-01-01T00:00:00.0000000+00:00");

        Assert.True(SingleInstanceGuard.TryAcquire(path, out var guard));
        guard!.Dispose();
    }

    [Theory]
    [InlineData("--allow-multiple-instances")]
    [InlineData("--ALLOW-MULTIPLE-INSTANCES")]
    [InlineData("  --allow-multiple-instances  ")]
    public void Override_switch_is_recognised(string arg)
    {
        Assert.True(SingleInstanceGuard.IsOverrideRequested(new[] { "--other", arg }));
    }

    [Theory]
    [InlineData("--allow-multiple")]
    [InlineData("allow-multiple-instances")]
    [InlineData("--allow-multiple-instances=true")]
    public void Near_misses_do_not_bypass_the_guard(string arg)
    {
        Assert.False(SingleInstanceGuard.IsOverrideRequested(new[] { arg }));
    }

    [Fact]
    public void No_arguments_means_no_override()
    {
        Assert.False(SingleInstanceGuard.IsOverrideRequested(null));
        Assert.False(SingleInstanceGuard.IsOverrideRequested([]));
        Assert.False(SingleInstanceGuard.IsOverrideRequested([null]));
    }

    private static string LockPath(TempDir dir) =>
        Path.Combine(dir.Path, SingleInstanceGuard.LockFileName);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcphub-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }
}
