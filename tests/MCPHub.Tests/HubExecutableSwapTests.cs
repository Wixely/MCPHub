using MCPHub.Core.Updates;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// The file choreography of installing a new executable over one that has exited, on plain files. Whether
/// a real published build survives it is proven by running one; see the update harness notes.
/// </summary>
public sealed class HubExecutableSwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcphub-swap-" + Guid.NewGuid().ToString("N"));

    public HubExecutableSwapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string File_(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Install_copies_the_new_build_in_and_parks_the_old_one()
    {
        var target = File_("MCPHub.exe", "OLD");
        var source = File_("downloads/MCPHub.exe", "NEW");

        HubExecutableSwap.Install(target, source);

        Assert.Equal("NEW", File.ReadAllText(target));
        Assert.Equal("OLD", File.ReadAllText(HubExecutableSwap.BackupPathFor(target)));
        Assert.True(File.Exists(source), "the source is copied, not moved: it is the running updater");
        Assert.False(File.Exists(HubExecutableSwap.IncomingPathFor(target)));
    }

    [Fact]
    public void Install_replaces_a_backup_left_by_an_earlier_update()
    {
        var target = File_("MCPHub.exe", "OLD");
        File_("MCPHub.exe.old", "OLDER");

        HubExecutableSwap.Install(target, File_("downloads/MCPHub.exe", "NEW"));

        Assert.Equal("OLD", File.ReadAllText(HubExecutableSwap.BackupPathFor(target)));
    }

    [Fact]
    public void Install_with_a_missing_file_touches_nothing()
    {
        var target = File_("MCPHub.exe", "OLD");
        Assert.Throws<FileNotFoundException>(() => HubExecutableSwap.Install(target, Path.Combine(_dir, "nope.exe")));
        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.False(File.Exists(HubExecutableSwap.BackupPathFor(target)));

        var source = File_("downloads/MCPHub.exe", "NEW");
        Assert.Throws<FileNotFoundException>(() => HubExecutableSwap.Install(Path.Combine(_dir, "missing.exe"), source));
    }

    [Fact]
    public void Install_gives_up_on_a_held_target_and_leaves_it_untouched_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return; // Unix renames an open file happily; only Windows has a lock to wait out.

        var target = File_("MCPHub.exe", "OLD");
        var source = File_("downloads/MCPHub.exe", "NEW");

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => HubExecutableSwap.Install(target, source, TimeSpan.FromMilliseconds(600)));
        }

        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.False(File.Exists(HubExecutableSwap.IncomingPathFor(target)), "the half-installed copy is cleaned up");
        Assert.False(File.Exists(HubExecutableSwap.BackupPathFor(target)));
    }

    [Fact]
    public void Install_waits_out_a_target_released_shortly_after_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var target = File_("MCPHub.exe", "OLD");
        var source = File_("downloads/MCPHub.exe", "NEW");
        var holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        _ = Task.Delay(700).ContinueWith(_ => holder.Dispose());

        HubExecutableSwap.Install(target, source, TimeSpan.FromSeconds(10));

        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void Undo_restores_the_parked_build_and_drops_the_new_one()
    {
        var target = File_("MCPHub.exe", "OLD");
        HubExecutableSwap.Install(target, File_("downloads/MCPHub.exe", "NEW"));

        HubExecutableSwap.Undo(target);

        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.False(File.Exists(HubExecutableSwap.BackupPathFor(target)));
    }

    [Fact]
    public void Undo_with_nothing_parked_is_a_no_op()
    {
        var target = File_("MCPHub.exe", "CURRENT");

        HubExecutableSwap.Undo(target);

        Assert.Equal("CURRENT", File.ReadAllText(target));
    }

    [Fact]
    public async Task Cleanup_removes_the_backup_and_an_interrupted_copy()
    {
        var target = File_("MCPHub.exe", "NEW");
        Assert.True(await HubExecutableSwap.TryRemoveBackupAsync(target));
        Assert.True(await HubExecutableSwap.TryRemoveBackupAsync(null));

        File_("MCPHub.exe.old", "OLD");
        File_("MCPHub.exe.new", "HALF");
        Assert.True(await HubExecutableSwap.TryRemoveBackupAsync(target));
        Assert.False(File.Exists(HubExecutableSwap.BackupPathFor(target)));
        Assert.False(File.Exists(HubExecutableSwap.IncomingPathFor(target)));
    }

    [Fact]
    public async Task Cleanup_gives_up_on_a_held_backup_after_its_patience_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var target = File_("MCPHub.exe", "NEW");
        var backup = File_("MCPHub.exe.old", "OLD");

        using (new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await HubExecutableSwap.TryRemoveBackupAsync(target, TimeSpan.FromMilliseconds(600)));
            Assert.True(File.Exists(backup));
        }

        Assert.True(await HubExecutableSwap.TryRemoveBackupAsync(target));
    }

    [Fact]
    public void The_installed_build_is_executable_on_unix_even_if_the_source_was_not()
    {
        if (OperatingSystem.IsWindows())
            return;

        var target = File_("MCPHub", "OLD");
        var source = File_("downloads/MCPHub", "NEW");
        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite); // as a bare unzip may leave it

        HubExecutableSwap.Install(target, source);

        Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
    }
}
