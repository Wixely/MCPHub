namespace MCPHub.Core.Catalog;

/// <summary>What happened when a config file was resolved for opening.</summary>
public enum ConfigFileOutcome
{
    /// <summary>The file was already there.</summary>
    Existing,

    /// <summary>Only a <c>.example</c> template existed; it was renamed into place.</summary>
    CreatedFromExample,

    /// <summary>Neither the file nor a template exists — nothing to open.</summary>
    Missing,
}

/// <summary>The resolved path for a config file, and how it got there.</summary>
public readonly record struct ConfigFileResolution(string? Path, ConfigFileOutcome Outcome)
{
    /// <summary>True when there is a real file on disk to hand to an editor.</summary>
    public bool Exists => Outcome is not ConfigFileOutcome.Missing && Path is not null;
}

/// <summary>
/// Finds the config file to open for a service, materialising it from its shipped template on
/// first use.
///
/// Some servers read more than one config file. RemoteAdmin, for example, reads its host
/// inventories from <c>remote_admin_windows_servers.json</c> and
/// <c>remote_admin_linux_servers.json</c>, but the release only ships
/// <c>*.example.json</c> versions — deliberately, so an update can never overwrite real
/// inventory. The consequence is that the files a user needs to edit do not exist until they
/// create them, which is not discoverable from the UI.
///
/// Opening one here renames the template into place, so the first click produces a working file
/// pre-filled with the documented shape.
/// </summary>
public static class ConfigFileResolver
{
    private const string ExampleMarker = ".example";

    /// <summary>
    /// The template name for a config file, e.g.
    /// <c>remote_admin_windows_servers.json</c> → <c>remote_admin_windows_servers.example.json</c>.
    /// </summary>
    public static string ExampleFileNameFor(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var extension = System.IO.Path.GetExtension(fileName);
        return stem + ExampleMarker + extension;
    }

    /// <summary>True when this name is itself a template rather than a live config.</summary>
    public static bool IsExampleFileName(string fileName) =>
        System.IO.Path.GetFileNameWithoutExtension(fileName)
            .EndsWith(ExampleMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve <paramref name="fileName"/> inside <paramref name="folder"/>, promoting the
    /// <c>.example</c> template if only that exists.
    /// </summary>
    public static ConfigFileResolution Resolve(string folder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(fileName))
            return new ConfigFileResolution(null, ConfigFileOutcome.Missing);

        var target = System.IO.Path.Combine(folder, fileName);
        if (File.Exists(target))
            return new ConfigFileResolution(target, ConfigFileOutcome.Existing);

        var example = System.IO.Path.Combine(folder, ExampleFileNameFor(fileName));
        if (!File.Exists(example))
            return new ConfigFileResolution(null, ConfigFileOutcome.Missing);

        try
        {
            File.Move(example, target);
            return new ConfigFileResolution(target, ConfigFileOutcome.CreatedFromExample);
        }
        catch (IOException)
        {
            // Someone else won the race, or the file is locked. If the target now exists that is
            // the desired outcome anyway; otherwise fall back to opening the template read-only
            // rather than failing the click entirely.
            return File.Exists(target)
                ? new ConfigFileResolution(target, ConfigFileOutcome.Existing)
                : new ConfigFileResolution(example, ConfigFileOutcome.Existing);
        }
        catch (UnauthorizedAccessException)
        {
            return new ConfigFileResolution(example, ConfigFileOutcome.Existing);
        }
    }

    /// <summary>
    /// A friendly label for a config file, used in the Config dropdown:
    /// <c>remote_admin_windows_servers.json</c> → <c>Windows servers</c>.
    /// </summary>
    public static string DescribeFileName(string fileName, string serviceName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);

        // The service's own config is the primary entry; name it plainly.
        if (string.Equals(stem, serviceName, StringComparison.OrdinalIgnoreCase))
            return "Main configuration";

        // Inventory files are prefixed with the product's snake-cased name; drop it so the
        // dropdown reads "Windows servers" rather than "Remote admin windows servers".
        var words = stem.Replace('_', ' ').Replace('-', ' ').Trim();
        var productWords = SplitPascalCase(serviceName.Replace("MCPSharp", string.Empty));
        if (words.StartsWith(productWords, StringComparison.OrdinalIgnoreCase))
            words = words[productWords.Length..].Trim();

        return words.Length == 0
            ? fileName
            : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string SplitPascalCase(string value)
    {
        var result = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (char.IsUpper(c) && result.Length > 0)
                result.Append(' ');
            result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }
}
