using System.Runtime.InteropServices;

namespace MCPHub.Core.Infrastructure;

/// <inheritdoc />
public sealed class AppPaths : IAppPaths
{
    private const string AppFolderName = "MCPHub";

    public AppPaths()
    {
        SettingsDirectory = Path.Combine(GetConfigRoot(), AppFolderName);
        DataDirectory = Path.Combine(GetDataRoot(), AppFolderName);
    }

    public string SettingsDirectory { get; }

    public string DataDirectory { get; }

    public string DownloadsDirectory => Path.Combine(DataDirectory, "downloads");

    public string DefaultServersDirectory => Path.Combine(DataDirectory, "servers");

    public string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetConfigRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrWhiteSpace(xdg) ? xdg : Path.Combine(GetHome(), ".config");
    }

    private static string GetDataRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrWhiteSpace(xdg) ? xdg : Path.Combine(GetHome(), ".local", "share");
    }

    private static string GetHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(home) ? home : Environment.GetEnvironmentVariable("HOME") ?? ".";
    }
}
