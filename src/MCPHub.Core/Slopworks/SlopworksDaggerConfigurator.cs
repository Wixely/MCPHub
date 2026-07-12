using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPHub.Core.Slopworks;

/// <summary>
/// One vLLM endpoint's-worth of shape needed to plug it into DaggerAgent's <c>Endpoints:Items[]</c>.
/// Populated from a <see cref="SlopworksStatus"/> snapshot but decoupled from it so the JSON
/// writer stays a pure function.
/// </summary>
public sealed record SlopworksEndpointDescriptor(
    string Id,
    string DisplayName,
    string BaseUrl,
    string Model,
    int RequestTimeoutSeconds = 600);

/// <summary>
/// Adds (or refreshes) a Slopworks-managed vLLM entry inside DaggerAgent's
/// <c>appsettings.json</c> <c>Endpoints:Items[]</c>. Never overwrites unrelated endpoints, never
/// touches <c>DefaultId</c>, and matches existing entries by <c>Id</c> so re-adding the same model
/// updates in place instead of creating a duplicate. Comments and unrelated formatting in the
/// source JSON are preserved best-effort (<see cref="JsonNode"/> loses comments, so if the user
/// had inline documentation it will be stripped — same trade-off the existing
/// <see cref="Agent.AgentProxyConfigurator"/> makes).
/// </summary>
public static class SlopworksDaggerConfigurator
{
    /// <summary>Provider string DaggerAgent recognises for OpenAI-compatible endpoints (which vLLM is).</summary>
    public const string Provider = "OpenAI";

    /// <summary>Prefix used for the endpoint <c>Id</c> so operators can spot Slopworks-authored entries.</summary>
    public const string IdPrefix = "slopworks-";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        // Slopworks-managed model names round-trip cleanly with the safe escape set; if a model
        // contains characters that trigger relaxed encoding they'd be escaped unnecessarily.
    };

    /// <summary>
    /// Builds a stable, filename-safe endpoint <c>Id</c> from a model name. Same model always
    /// hashes to the same id so re-adding is idempotent; different model → different id → separate
    /// entry (which is what the "add a new one when the model changes" flow needs).
    /// </summary>
    public static string BuildId(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return IdPrefix + "default";

        var sb = new StringBuilder(IdPrefix.Length + model.Length);
        sb.Append(IdPrefix);
        foreach (var c in model)
        {
            if (char.IsLetterOrDigit(c) || c == '-')
                sb.Append(char.ToLowerInvariant(c));
            else if (c == '/' || c == ':' || c == '_' || c == '.' || c == ' ')
                sb.Append('-');
            // Anything else is dropped — the id is a slug, not a mirror.
        }
        // Collapse accidental double-hyphens for readability.
        var raw = sb.ToString();
        while (raw.Contains("--", StringComparison.Ordinal))
            raw = raw.Replace("--", "-");
        return raw.TrimEnd('-');
    }

    /// <summary>
    /// Returns <paramref name="appsettingsJson"/> with a <see cref="SlopworksEndpointDescriptor"/>
    /// added / refreshed under <c>Endpoints.Items</c>. Missing sections are created; existing
    /// entries with a matching <c>Id</c> are updated in place; nothing else is touched.
    /// </summary>
    public static string WireEndpoint(string appsettingsJson, SlopworksEndpointDescriptor endpoint)
    {
        var root = string.IsNullOrWhiteSpace(appsettingsJson)
            ? new JsonObject()
            : JsonNode.Parse(appsettingsJson) as JsonObject ?? new JsonObject();

        if (root["Endpoints"] is not JsonObject endpoints)
        {
            endpoints = new JsonObject();
            root["Endpoints"] = endpoints;
        }

        if (endpoints["Items"] is not JsonArray items)
        {
            items = new JsonArray();
            endpoints["Items"] = items;
        }

        // Update-in-place if an entry with the same Id already exists (idempotent re-wire after a
        // model / port change on the same model).
        foreach (var node in items)
        {
            if (node is JsonObject o &&
                string.Equals((string?)o["Id"], endpoint.Id, StringComparison.OrdinalIgnoreCase))
            {
                ApplyDescriptor(o, endpoint);
                return root.ToJsonString(Indented);
            }
        }

        // New entry — append, don't reorder existing items and don't touch DefaultId.
        var fresh = new JsonObject();
        ApplyDescriptor(fresh, endpoint);
        items.Add(fresh);
        return root.ToJsonString(Indented);
    }

    private static void ApplyDescriptor(JsonObject target, SlopworksEndpointDescriptor e)
    {
        target["Id"] = e.Id;
        target["DisplayName"] = e.DisplayName;
        target["Provider"] = Provider;
        target["BaseUrl"] = e.BaseUrl;
        target["ApiKey"] = "";
        target["DefaultModel"] = e.Model;
        target["RequestTimeoutSeconds"] = e.RequestTimeoutSeconds;
        target["Enabled"] = true;
    }
}
