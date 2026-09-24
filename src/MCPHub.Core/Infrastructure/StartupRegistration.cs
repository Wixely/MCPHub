using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MCPHub.Core.Infrastructure;

/// <summary>
/// Whether MCPHub starts when the user signs in. The operating system is the single source of truth — this
/// is never mirrored into <c>settings.json</c>, because it is per-machine and because a user who turns
/// MCPHub off in Task Manager's Startup tab must see that reflected here rather than silently re-enabled.
/// For the same reason it is deliberately left out of the settings archive: importing someone else's
/// configuration should not enrol your machine in running an app at sign-in.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>Whether this installation can register itself at all; see <see cref="UnavailableReason"/>.</summary>
    bool IsSupported { get; }

    /// <summary>Why registration is unavailable, or <see langword="null"/> when it is available.</summary>
    string? UnavailableReason { get; }

    /// <summary>Whether MCPHub is currently registered to run at sign-in, read from the OS each time.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Registers or unregisters MCPHub. Returns <see langword="null"/> on success, or a message to show the
    /// user. Never throws: failing to change a convenience setting must not take the Settings page with it.
    /// </summary>
    string? TrySetEnabled(bool enabled);
}

/// <inheritdoc />
public sealed class StartupRegistration : IStartupRegistration
{
    /// <summary>Registry value, and Linux desktop-entry file name, identifying MCPHub's entry.</summary>
    public const string EntryName = "MCPHub";

    /// <summary>The per-user run key. Per-user so no elevation is needed and no other account is affected.</summary>
    public const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string? _executablePath;
    private readonly string? _entryAssemblyName;
    private readonly string _windowsRunKey;
    private readonly string _autostartDirectory;

    /// <param name="executablePath">Program to register; defaults to this process's own executable.</param>
    /// <param name="entryAssemblyName">
    /// The name that executable is expected to have; defaults to the entry assembly's. Only the host-launcher
    /// check uses it — see <see cref="InstanceRestart.IsLaunchedByAHost"/>.
    /// </param>
    /// <param name="windowsRunKey">Registry key under HKCU to write to. Overridden by tests.</param>
    /// <param name="autostartDirectory">XDG autostart directory. Overridden by tests.</param>
    public StartupRegistration(
        string? executablePath = null,
        string? entryAssemblyName = null,
        string? windowsRunKey = null,
        string? autostartDirectory = null)
    {
        _executablePath = executablePath ?? Environment.ProcessPath;
        _entryAssemblyName = entryAssemblyName;
        _windowsRunKey = windowsRunKey ?? WindowsRunKey;
        _autostartDirectory = autostartDirectory ?? DefaultAutostartDirectory();
    }

    /// <inheritdoc />
    public bool IsSupported => UnavailableReason is null;

    /// <inheritdoc />
    public string? UnavailableReason
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_executablePath) || !File.Exists(_executablePath))
                return "MCPHub could not work out where its own executable is, so it cannot register itself to run at sign-in.";

            // Registering the dotnet host would start dotnet, not MCPHub — the same trap as tray Restart.
            if (InstanceRestart.IsLaunchedByAHost(_executablePath, _entryAssemblyName))
                return $"MCPHub is running under {Path.GetFileName(_executablePath)} rather than from its own executable, " +
                       "so it cannot register itself to run at sign-in. This works in an installed build.";

            return OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
                ? null
                : "Running at sign-in is not supported on this operating system.";
        }
    }

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return ReadWindows() is not null;
                if (OperatingSystem.IsLinux())
                    return File.Exists(DesktopEntryPath);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // Unreadable reads as "not registered": the checkbox showing off when it cannot tell is
                // honest, and ticking it will report the real error.
            }

            return false;
        }
    }

    /// <inheritdoc />
    public string? TrySetEnabled(bool enabled)
    {
        if (UnavailableReason is { } reason)
            return reason;

        try
        {
            if (OperatingSystem.IsWindows())
                WriteWindows(enabled);
            else if (OperatingSystem.IsLinux())
                WriteLinux(enabled);

            return null;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return $"Could not {(enabled ? "enable" : "disable")} running MCPHub at sign-in: {ex.Message}";
        }
    }

    // ---- Windows ------------------------------------------------------------------------------------

    /// <summary>The command written to the run key. Quoted, since program paths routinely contain spaces.</summary>
    private string WindowsCommand => $"\"{_executablePath}\"";

    [SupportedOSPlatform("windows")]
    private string? ReadWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_windowsRunKey, writable: false);
        return key?.GetValue(EntryName) as string;
    }

    [SupportedOSPlatform("windows")]
    private void WriteWindows(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(_windowsRunKey, writable: true);
            existing?.DeleteValue(EntryName, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(_windowsRunKey, writable: true)
            ?? throw new IOException("The Windows run key could not be opened.");
        // Rewritten every time it is enabled, so moving or reinstalling MCPHub corrects a stale path.
        key.SetValue(EntryName, WindowsCommand, RegistryValueKind.String);
    }

    // ---- Linux --------------------------------------------------------------------------------------

    private string DesktopEntryPath => Path.Combine(_autostartDirectory, EntryName.ToLowerInvariant() + ".desktop");

    private void WriteLinux(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(DesktopEntryPath))
                File.Delete(DesktopEntryPath);
            return;
        }

        Directory.CreateDirectory(_autostartDirectory);
        File.WriteAllText(DesktopEntryPath, $"""
            [Desktop Entry]
            Type=Application
            Name={EntryName}
            Comment=MCP server hub and proxy
            Exec="{_executablePath}"
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }

    private static string DefaultAutostartDirectory()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configHome = Path.Combine(string.IsNullOrWhiteSpace(home) ? "." : home, ".config");
        }

        return Path.Combine(configHome, "autostart");
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;
}
