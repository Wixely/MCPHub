using System.Text.Json.Serialization;
using MCPHub.Core.Models;

namespace MCPHub.Core.Management;

/// <summary>
/// One managed server as reported to an agent. Deliberately omits install paths, config files and logs —
/// the management tools let an agent operate a server, not read its configuration or output.
/// </summary>
public sealed class ServiceSummary
{
    /// <summary>Canonical product name, e.g. <c>KodiMCPSharp</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Proxy namespace key — the prefix before <c>__</c> in that server's tool names, e.g. <c>kodi</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool Installed { get; set; }

    public string? InstalledVersion { get; set; }

    public string? LatestVersion { get; set; }

    public UpdateStatus UpdateStatus { get; set; }

    public ServiceRunState RunState { get; set; }

    public int? Port { get; set; }

    public string RepositoryUrl { get; set; } = string.Empty;
}

/// <summary>Result of <c>mcphub__list_services</c>.</summary>
public sealed class ServiceListResult
{
    public int Count { get; set; }
    public List<ServiceSummary> Services { get; set; } = [];
}

/// <summary>Result of <c>mcphub__check_service_updates</c>.</summary>
public sealed class ServiceUpdateCheckResult
{
    /// <summary>How many servers were checked.</summary>
    public int Checked { get; set; }

    /// <summary>How many GitHub answered for.</summary>
    public int Reachable { get; set; }

    public int UpdatesAvailable { get; set; }

    public string? Note { get; set; }

    public List<ServiceSummary> Services { get; set; } = [];
}

/// <summary>Result of a start / stop / restart / install / update call.</summary>
public sealed class ServiceActionResult
{
    public string Action { get; set; } = string.Empty;

    /// <summary>Plain-language outcome, e.g. "KodiMCPSharp is Running on port 5730."</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Whether the server was running when the request arrived (for update / restart).</summary>
    public bool? WasRunning { get; set; }

    /// <summary>Version before an update replaced it.</summary>
    public string? PreviousVersion { get; set; }

    public ServiceSummary Service { get; set; } = new();
}

/// <summary>Result of <c>mcphub__check_hub_update</c>.</summary>
public sealed class HubUpdateResult
{
    public string CurrentVersion { get; set; } = string.Empty;

    public string LatestVersion { get; set; } = string.Empty;

    public UpdateStatus UpdateStatus { get; set; }

    /// <summary>Release date as <c>yyyy-MM-dd</c>, when GitHub reported one.</summary>
    public string? PublishedAt { get; set; }

    /// <summary>GitHub's newest-release page — where the user downloads a build.</summary>
    public string ReleasePageUrl { get; set; } = string.Empty;

    /// <summary>Name of the release asset for this OS, if the release carries one.</summary>
    public string? AssetName { get; set; }

    public long? AssetSizeBytes { get; set; }

    public string Note { get; set; } = string.Empty;
}

/// <summary>Source-generated JSON context for management tool results (indented, camelCase, string enums).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServiceListResult))]
[JsonSerializable(typeof(ServiceUpdateCheckResult))]
[JsonSerializable(typeof(ServiceActionResult))]
[JsonSerializable(typeof(HubUpdateResult))]
public sealed partial class ManagementJsonContext : JsonSerializerContext;
