using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPHub.Core.Services;

/// <summary>
/// Merges a user's existing config into a new default config when a service is updated, so user
/// settings survive while new default keys are picked up.
/// </summary>
public interface IConfigMergeService
{
    /// <summary>
    /// Merges <paramref name="userExisting"/> into <paramref name="newDefault"/>. The new default
    /// drives the shape: keys come from the default (so keys removed in the new version drop out),
    /// scalar values prefer the user's value, arrays are taken wholesale from the user when present,
    /// and a type change resolves to the default.
    /// </summary>
    JsonNode? Merge(JsonNode? userExisting, JsonNode? newDefault);

    /// <summary>String convenience over <see cref="Merge"/>: parses both, merges, returns indented JSON
    /// (validated by a round-trip parse). A corrupt user config falls back to the new default.</summary>
    string MergeJson(string? existingUserJson, string newDefaultJson);
}

/// <inheritdoc />
public sealed class ConfigMergeService : IConfigMergeService
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public JsonNode? Merge(JsonNode? userExisting, JsonNode? newDefault)
    {
        switch (newDefault)
        {
            case JsonObject defaultObject:
            {
                var result = new JsonObject();
                var userObject = userExisting as JsonObject;
                foreach (var (key, defaultChild) in defaultObject)
                {
                    if (userObject is not null && userObject.TryGetPropertyValue(key, out var userChild))
                        result[key] = Merge(userChild, defaultChild);   // recurse: keep user, default-led
                    else
                        result[key] = defaultChild?.DeepClone();         // new default key
                }
                return result;
            }

            case JsonArray:
                // Whole-array: take the user's array when present (index-wise merge is fragile), else default.
                return (userExisting is JsonArray ? userExisting : newDefault)?.DeepClone();

            default:
                // Scalar or null: prefer the user's value when it is a value, else the default.
                return (userExisting is JsonValue ? userExisting : newDefault)?.DeepClone();
        }
    }

    public string MergeJson(string? existingUserJson, string newDefaultJson)
    {
        var defaultNode = JsonNode.Parse(newDefaultJson, nodeOptions: null, DocOptions)
            ?? throw new JsonException("New default config is not a JSON document.");

        JsonNode? userNode = null;
        if (!string.IsNullOrWhiteSpace(existingUserJson))
        {
            try { userNode = JsonNode.Parse(existingUserJson, nodeOptions: null, DocOptions); }
            catch (JsonException) { userNode = null; } // corrupt user config -> use defaults
        }

        var merged = userNode is null ? defaultNode : Merge(userNode, defaultNode);
        var text = merged!.ToJsonString(WriteOptions);

        _ = JsonNode.Parse(text); // validate round-trip before returning
        return text;
    }
}
