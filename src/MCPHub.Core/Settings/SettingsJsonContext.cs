using System.Text.Json.Serialization;

namespace MCPHub.Core.Settings;

/// <summary>Source-generated JSON context for MCPHub settings (string enums, indented).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MCPHubSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
