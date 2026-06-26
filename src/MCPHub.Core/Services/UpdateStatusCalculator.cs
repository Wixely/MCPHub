using MCPHub.Core.Models;
using Semver;

namespace MCPHub.Core.Services;

/// <summary>Computes an <see cref="UpdateStatus"/> from installed vs latest version strings (SemVer-aware).</summary>
public static class UpdateStatusCalculator
{
    public static UpdateStatus Compute(string? installedVersion, string? latestVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion))
            return UpdateStatus.NotInstalled;

        if (string.IsNullOrWhiteSpace(latestVersion))
            return UpdateStatus.Unknown;

        if (SemVersion.TryParse(installedVersion, SemVersionStyles.Any, out var installed) &&
            SemVersion.TryParse(latestVersion, SemVersionStyles.Any, out var latest))
        {
            return installed!.ComparePrecedenceTo(latest) >= 0
                ? UpdateStatus.UpToDate
                : UpdateStatus.UpdateAvailable;
        }

        // Unparseable versions: exact match means up to date, otherwise assume an update exists.
        return string.Equals(installedVersion, latestVersion, StringComparison.OrdinalIgnoreCase)
            ? UpdateStatus.UpToDate
            : UpdateStatus.UpdateAvailable;
    }
}
