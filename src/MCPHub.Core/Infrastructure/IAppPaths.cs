namespace MCPHub.Core.Infrastructure;

/// <summary>
/// Resolves MCPHub's cross-platform directories. Config lives under the per-user config root
/// (<c>%AppData%</c> / <c>~/.config</c>); caches, downloads and the default servers folder live under
/// the per-user data root (<c>%LocalAppData%</c> / <c>~/.local/share</c>), honouring XDG on Linux.
/// </summary>
public interface IAppPaths
{
    /// <summary>Per-user settings directory, e.g. <c>%AppData%/MCPHub</c> or <c>~/.config/MCPHub</c>.</summary>
    string SettingsDirectory { get; }

    /// <summary>Per-user data/cache directory, e.g. <c>%LocalAppData%/MCPHub</c> or <c>~/.local/share/MCPHub</c>.</summary>
    string DataDirectory { get; }

    /// <summary>Where release archives are downloaded before extraction.</summary>
    string DownloadsDirectory { get; }

    /// <summary>Default shared folder holding every product's executable + config.</summary>
    string DefaultServersDirectory { get; }

    /// <summary>Creates the directory if missing and returns it.</summary>
    string EnsureDirectory(string path);
}
