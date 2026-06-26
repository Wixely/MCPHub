using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPHub.Core.Process;

/// <summary>
/// Reads the effective listen port/host from an installed <c>{Name}.json</c> config's <c>Server</c>
/// section, so MCPHub follows each server's own configuration rather than hard-coding ports.
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
            return root?["Server"];
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
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
