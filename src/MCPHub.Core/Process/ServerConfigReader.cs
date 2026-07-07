using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPHub.Core.Process;

/// <summary>
/// Reads the effective listen port/host from an installed <c>{Name}.json</c> config's <c>Server</c>
/// section, so MCPHub follows each server's own configuration rather than hard-coding ports. The
/// <c>Server</c> object is found at any nesting depth — some products nest it under a product section
/// (e.g. <c>Playwright.Server</c>) rather than at the top level.
/// </summary>
public static class ServerConfigReader
{
    /// <summary>Reads <c>Server.Port</c>, or <see langword="null"/> if missing/unreadable.</summary>
    public static int? ReadPort(string configPath)
    {
        var server = ReadServerSection(configPath);
        return ReadInt(server?["Port"]);
    }

    /// <summary>Reads <c>Server.Host</c>, or <see langword="null"/> if missing/unreadable.</summary>
    public static string? ReadHost(string configPath)
        => ReadServerSection(configPath)?["Host"]?.GetValue<string>();

    private static JsonNode? ReadServerSection(string configPath)
    {
        if (!File.Exists(configPath))
            return null;

        try
        {
            using var stream = File.OpenRead(configPath);
            var root = JsonNode.Parse(stream, nodeOptions: null, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return FindServerSection(root);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the first <c>Server</c> object that carries a <c>Port</c>, at any depth. Prefers a match at
    /// the current level before recursing, so a top-level <c>Server</c> wins; a non-object <c>Server</c>
    /// (e.g. a SQL connection string) is skipped in favour of the real listen config.
    /// </summary>
    private static JsonNode? FindServerSection(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["Server"] is JsonObject server && server["Port"] is not null)
                return server;

            foreach (var (_, value) in obj)
            {
                if (FindServerSection(value) is { } found)
                    return found;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (FindServerSection(item) is { } found)
                    return found;
            }
        }

        return null;
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is null)
            return null;

        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.Number => node.GetValue<int>(),
                JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var p) => p,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
