using System.Text.Json.Serialization;

namespace MCPHub.Core.Services.Github;

// Persisted form of the release cache: maps each releases/latest URL to the last-seen ETag and the
// release resolved from it. Persisting across restarts lets MCPHub issue conditional requests
// (unchanged releases return 304 — which GitHub does not count against the rate limit) and show the
// last-known version while offline or rate-limited, instead of a bare "—".

internal sealed class ReleaseCacheEntry
{
    public string ETag { get; set; } = string.Empty;
    public ReleaseInfo? Info { get; set; }
}

internal sealed class ReleaseCacheFile
{
    public Dictionary<string, ReleaseCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Source-generated (trim/AOT-friendly) JSON context for the on-disk release cache.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReleaseCacheFile))]
internal sealed partial class ReleaseCacheJsonContext : JsonSerializerContext;
