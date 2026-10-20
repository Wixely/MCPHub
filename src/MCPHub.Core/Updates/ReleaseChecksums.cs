namespace MCPHub.Core.Updates;

/// <summary>
/// The <c>SHA256SUMS</c> file a release carries beside its zips, in the format <c>sha256sum</c> writes:
/// one <c>&lt;hex digest&gt;  &lt;file name&gt;</c> per line, with an optional <c>*</c> before a name written
/// in binary mode. Anything that does not look like that is skipped rather than refused, so a stray
/// comment or blank line cannot stop an otherwise good file from verifying a download.
/// </summary>
public sealed class ReleaseChecksums
{
    private readonly Dictionary<string, string> _digests;

    private ReleaseChecksums(Dictionary<string, string> digests) => _digests = digests;

    /// <summary>How many files the checksum file names.</summary>
    public int Count => _digests.Count;

    /// <summary>Parses the text of a checksum file.</summary>
    public static ReleaseChecksums Parse(string text)
    {
        var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var split = line.IndexOfAny([' ', '\t']);
            if (split <= 0)
                continue;

            var digest = line[..split];
            var name = line[split..].TrimStart().TrimStart('*');
            // Names may carry a path when sums were taken from a folder; the asset is matched by file name.
            name = name.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];

            if (digest.Length == 64 && digest.All(Uri.IsHexDigit) && name.Length > 0)
                digests[name] = digest;
        }

        return new ReleaseChecksums(digests);
    }

    /// <summary>The expected SHA-256 digest of a named file, lower-case hex, or <see langword="null"/> when it is not listed.</summary>
    public string? DigestFor(string fileName)
        => _digests.TryGetValue(fileName, out var digest) ? digest.ToLowerInvariant() : null;
}
