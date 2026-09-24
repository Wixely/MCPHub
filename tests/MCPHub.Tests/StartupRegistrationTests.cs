using MCPHub.Core.Infrastructure;
using Microsoft.Win32;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// Registering MCPHub to run at sign-in. The real mechanism is exercised — an actual registry key on Windows,
/// an actual desktop entry on Linux — but never the real autostart location: a test must not enrol the
/// machine it runs on in starting anything.
/// </summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _scratchKey = $@"Software\MCPHub\Tests\{Guid.NewGuid():N}";
    private readonly string _scratchDirectory = Path.Combine(Path.GetTempPath(), "mcphub-autostart-tests", Guid.NewGuid().ToString("N"));
    private readonly string _executable;

    public StartupRegistrationTests()
    {
        // A real file named like the app, so the host-launcher guard does not reject it.
        Directory.CreateDirectory(_scratchDirectory);
        _executable = Path.Combine(_scratchDirectory, OperatingSystem.IsWindows() ? "MCPHub.exe" : "MCPHub");
        File.WriteAllText(_executable, "not a real program");
    }

    // The app's own name, not the test assembly's, so the host-launcher check compares what it would in production.
    private StartupRegistration NewRegistration(string? executable = null) => new(
        executable ?? _executable,
        entryAssemblyName: "MCPHub",
        windowsRunKey: _scratchKey,
        autostartDirectory: Path.Combine(_scratchDirectory, "autostart"));

    [Fact]
    public void Starts_unregistered_and_round_trips_through_the_operating_system()
    {
        var startup = NewRegistration();
        Assert.True(startup.IsSupported);
        Assert.Null(startup.UnavailableReason);
        Assert.False(startup.IsEnabled);

        Assert.Null(startup.TrySetEnabled(true));
        Assert.True(startup.IsEnabled);

        // Read back by a separate instance: the state lives in the OS, not in this object.
        Assert.True(NewRegistration().IsEnabled);

        Assert.Null(startup.TrySetEnabled(false));
        Assert.False(startup.IsEnabled);
        Assert.False(NewRegistration().IsEnabled);
    }

    [Fact]
    public void Enabling_twice_and_disabling_twice_are_both_harmless()
    {
        var startup = NewRegistration();

        Assert.Null(startup.TrySetEnabled(true));
        Assert.Null(startup.TrySetEnabled(true));
        Assert.True(startup.IsEnabled);

        Assert.Null(startup.TrySetEnabled(false));
        // Disabling something already absent must not fail — the user may have removed it outside MCPHub.
        Assert.Null(startup.TrySetEnabled(false));
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void Re_enabling_corrects_a_path_left_behind_by_a_move_or_reinstall()
    {
        var oldLocation = Path.Combine(_scratchDirectory, "old");
        Directory.CreateDirectory(oldLocation);
        var oldExecutable = Path.Combine(oldLocation, Path.GetFileName(_executable));
        File.WriteAllText(oldExecutable, "not a real program");

        Assert.Null(NewRegistration(oldExecutable).TrySetEnabled(true));
        Assert.Contains(oldLocation, ReadRegisteredCommand());

        // The same user, now running MCPHub from somewhere else, should not be left pointing at the old copy.
        Assert.Null(NewRegistration().TrySetEnabled(true));
        var command = ReadRegisteredCommand();
        Assert.Contains(_executable, command);
        Assert.DoesNotContain(oldLocation, command);
    }

    [Fact]
    public void A_path_with_spaces_is_registered_so_the_shell_runs_one_program()
    {
        var spaced = Path.Combine(_scratchDirectory, "Program Files Like");
        Directory.CreateDirectory(spaced);
        var executable = Path.Combine(spaced, Path.GetFileName(_executable));
        File.WriteAllText(executable, "not a real program");

        Assert.Null(NewRegistration(executable).TrySetEnabled(true));

        // Unquoted, the space would split the command and start something else entirely.
        Assert.Contains($"\"{executable}\"", ReadRegisteredCommand());
    }

    [Fact]
    public void Running_under_the_dotnet_host_is_refused_rather_than_registering_the_host()
    {
        // Registering dotnet would start dotnet at sign-in, not MCPHub — the same trap as tray Restart.
        var host = Path.Combine(_scratchDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(host, "not a real program");
        var startup = NewRegistration(host);

        Assert.False(startup.IsSupported);
        Assert.Contains("cannot register itself to run at sign-in", startup.UnavailableReason);
        Assert.Contains("cannot register itself to run at sign-in", startup.TrySetEnabled(true));
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void An_executable_that_is_not_there_is_reported_rather_than_registered()
    {
        var startup = NewRegistration(Path.Combine(_scratchDirectory, "gone", "MCPHub.exe"));

        Assert.False(startup.IsSupported);
        Assert.NotNull(startup.TrySetEnabled(true));
        Assert.False(startup.IsEnabled);
    }

    private string ReadRegisteredCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(_scratchKey);
            return (string?)key?.GetValue(StartupRegistration.EntryName) ?? string.Empty;
        }

        var entry = Path.Combine(_scratchDirectory, "autostart", "mcphub.desktop");
        return File.Exists(entry) ? File.ReadAllText(entry) : string.Empty;
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            // Remove the whole scratch tree, so a failed test cannot leave a stray Run-like entry behind.
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MCPHub\Tests", throwOnMissingSubKey: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        try { Directory.Delete(_scratchDirectory, recursive: true); } catch (IOException) { }
    }
}
