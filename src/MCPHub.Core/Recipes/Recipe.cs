using System.Text.Json.Serialization;

namespace MCPHub.Core.Recipes;

/// <summary>
/// One entry in MCPHub's recipes knowledge base: a concise "if X then Y" note on how to combine
/// MCP servers to complete a task no single server can. Service-agnostic — the servers involved
/// are just keys (e.g. <c>kodi</c>, <c>adb</c>) so a recipe can name any upstream, catalogued or not.
/// </summary>
/// <example>
/// Title: "Access Kodi" · When: "Kodi tools fail because Kodi isn't running" · Then: "On Android,
/// launch Kodi with adb (adb__launch_app); on Windows/Linux, start it with remoteadmin; then retry."
/// </example>
public sealed class Recipe
{
    /// <summary>Short stable id (8 hex chars) that agents pass back to update or remove the recipe.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Short name for the recipe, e.g. <c>Access Kodi</c>.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The situation or condition — the "if X" half.</summary>
    public string When { get; set; } = string.Empty;

    /// <summary>The action to take — the "then Y" half, naming the servers/tools to use.</summary>
    public string Then { get; set; } = string.Empty;

    /// <summary>Server keys involved, lower-case, e.g. <c>["kodi", "adb", "remoteadmin"]</c>.</summary>
    public List<string> Services { get; set; } = [];

    /// <summary>Optional caveats or extra detail.</summary>
    public string? Notes { get; set; }

    /// <summary>Who last wrote it: <see cref="RecipeSources.User"/> or <see cref="RecipeSources.Agent"/>.</summary>
    public string Source { get; set; } = RecipeSources.User;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Deep copy, so callers can hand out snapshots without exposing the stored instance.</summary>
    public Recipe Clone() => new()
    {
        Id = Id,
        Title = Title,
        When = When,
        Then = Then,
        Services = [.. Services],
        Notes = Notes,
        Source = Source,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}

/// <summary>Well-known values for <see cref="Recipe.Source"/>.</summary>
public static class RecipeSources
{
    public const string User = "user";
    public const string Agent = "agent";
}

/// <summary>The editable fields of a recipe, as submitted by the UI or an agent before validation.</summary>
public sealed class RecipeDraft
{
    public string? Title { get; set; }
    public string? When { get; set; }
    public string? Then { get; set; }
    public IReadOnlyList<string>? Services { get; set; }
    public string? Notes { get; set; }

    /// <summary>A draft pre-filled from an existing recipe, for partial updates.</summary>
    public static RecipeDraft From(Recipe recipe) => new()
    {
        Title = recipe.Title,
        When = recipe.When,
        Then = recipe.Then,
        Services = [.. recipe.Services],
        Notes = recipe.Notes,
    };
}

/// <summary>Thrown when a draft fails validation; the message is safe to show to a user or agent.</summary>
public sealed class RecipeValidationException(string message) : Exception(message);

/// <summary>On-disk shape of <c>recipes.json</c>.</summary>
public sealed class RecipeFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<Recipe> Recipes { get; set; } = [];
}

/// <summary>Tool-result shape for a recipe listing.</summary>
public sealed class RecipeListResult
{
    public int Count { get; set; }
    public List<Recipe> Recipes { get; set; } = [];
}

/// <summary>Source-generated JSON context for recipes (indented, camelCase, case-insensitive on read).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RecipeFile))]
[JsonSerializable(typeof(Recipe))]
[JsonSerializable(typeof(RecipeListResult))]
public sealed partial class RecipeJsonContext : JsonSerializerContext;
