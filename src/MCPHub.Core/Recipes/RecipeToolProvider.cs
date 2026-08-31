using System.Text.Json;
using MCPHub.Proxy;
using ModelContextProtocol.Protocol;

namespace MCPHub.Core.Recipes;

/// <summary>
/// Exposes the recipes knowledge base to agents through the proxy as <c>recipes__list</c>, <c>recipes__get</c>,
/// <c>recipes__add</c>, <c>recipes__update</c> and <c>recipes__remove</c>. Results are JSON text so an agent
/// can read them back structurally; failures are ordinary MCP tool errors with a plain-language message.
/// </summary>
public sealed class RecipeToolProvider : ILocalToolProvider
{
    /// <summary>Namespace key: tools appear as <c>recipes__*</c>.</summary>
    public const string ProviderKey = "recipes";

    /// <summary>
    /// Suggested MCP server instructions for a host that carries these tools, so an agent knows the
    /// knowledge base exists before it needs it.
    /// </summary>
    public const string ServerInstructions =
        "MCPHub aggregates several MCP servers. It also keeps 'recipes': short 'if X then Y' notes on how to " +
        "combine servers to finish a task no single server can (for example, using one server to start a program " +
        "another server depends on). Call recipes__list before a task that spans servers, or when a server's tools " +
        "fail because something they need is not running. When you discover such a combination that works, save it " +
        "with recipes__add so it is available next time; fix or retire stale ones with recipes__update / recipes__remove.";

    /// <summary>Variant of <see cref="ServerInstructions"/> for hosts where agents may read recipes but not change them.</summary>
    public const string ReadOnlyServerInstructions =
        "MCPHub aggregates several MCP servers. It also keeps 'recipes': short 'if X then Y' notes on how to " +
        "combine servers to finish a task no single server can (for example, using one server to start a program " +
        "another server depends on). Call recipes__list before a task that spans servers, or when a server's tools " +
        "fail because something they need is not running. Recipes are read-only for you here: consult them, but you " +
        "cannot add or change them — tell the user if one is missing or wrong.";

    private static readonly IReadOnlyList<Tool> ToolDefinitions =
    [
        new Tool
        {
            Name = "list",
            Description = "List recipes: concise 'if X then Y' notes on combining MCP servers to complete tasks no single " +
                          "server can. Consult before a multi-server task, or when a server's tools fail because " +
                          "something they depend on is not running. Optional filters: 'query' (text match on any field) " +
                          "and 'service' (only recipes involving this server key, e.g. 'kodi').",
            InputSchema = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "query":   { "type": "string", "description": "Case-insensitive text to match in title, when, then, notes or services." },
                    "service": { "type": "string", "description": "Only recipes that involve this server key (the prefix before '__' in tool names, e.g. 'kodi')." }
                  }
                }
                """),
        },
        new Tool
        {
            Name = "get",
            Description = "Get one recipe by id.",
            InputSchema = Schema("""
                {
                  "type": "object",
                  "properties": { "id": { "type": "string", "description": "Recipe id, as returned by list/add." } },
                  "required": ["id"]
                }
                """),
        },
        new Tool
        {
            Name = "add",
            Description = "Save a new recipe once you have found a reliable way to combine MCP servers for a task. Keep it " +
                          "concise: 'title' (short name), 'when' (the situation — the 'if X'), 'then' (the action — the " +
                          "'then Y', naming the servers/tools to use), 'services' (server keys involved, e.g. " +
                          "[\"kodi\",\"adb\"]), optional 'notes' (caveats). Returns the stored recipe with its id.",
            InputSchema = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "title":    { "type": "string", "description": "Short name, e.g. 'Access Kodi'. Max 80 chars." },
                    "when":     { "type": "string", "description": "The situation or condition. Max 400 chars." },
                    "then":     { "type": "string", "description": "What to do, naming the servers/tools to use. Max 800 chars." },
                    "services": { "type": "array", "items": { "type": "string" }, "description": "Server keys involved (lower-case), e.g. [\"kodi\", \"adb\", \"remoteadmin\"]." },
                    "notes":    { "type": "string", "description": "Optional caveats or extra detail. Max 1000 chars." }
                  },
                  "required": ["title", "when", "then"]
                }
                """),
        },
        new Tool
        {
            Name = "update",
            Description = "Edit an existing recipe by id. Only the fields you pass change; the rest are kept. " +
                          "Returns the updated recipe.",
            InputSchema = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "id":       { "type": "string", "description": "Recipe id." },
                    "title":    { "type": "string" },
                    "when":     { "type": "string" },
                    "then":     { "type": "string" },
                    "services": { "type": "array", "items": { "type": "string" }, "description": "Replaces the whole services list when given." },
                    "notes":    { "type": "string", "description": "Replaces the notes; pass an empty string to clear them." }
                  },
                  "required": ["id"]
                }
                """),
        },
        new Tool
        {
            Name = "remove",
            Description = "Delete a recipe by id — when it is wrong, obsolete, or duplicated.",
            InputSchema = Schema("""
                {
                  "type": "object",
                  "properties": { "id": { "type": "string", "description": "Recipe id." } },
                  "required": ["id"]
                }
                """),
        },
    ];

    private readonly IRecipeStore _store;

    public RecipeToolProvider(IRecipeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public string Key => ProviderKey;

    /// <inheritdoc />
    public string DisplayName => "Recipes";

    /// <inheritdoc />
    public IReadOnlyList<Tool> Tools => ToolDefinitions;

    /// <inheritdoc />
    public ValueTask<CallToolResult> CallAsync(string toolName, IReadOnlyDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = toolName switch
            {
                "list" => List(arguments),
                "get" => Get(arguments),
                "add" => Add(arguments),
                "update" => Update(arguments),
                "remove" => Remove(arguments),
                _ => Error($"Unknown recipes tool '{toolName}'."),
            };
            return ValueTask.FromResult(result);
        }
        catch (RecipeValidationException ex)
        {
            return ValueTask.FromResult(Error(ex.Message));
        }
    }

    private CallToolResult List(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var recipes = _store.Search(GetString(args, "query"), GetString(args, "service")).ToList();
        var payload = new RecipeListResult { Count = recipes.Count, Recipes = recipes };
        return Text(JsonSerializer.Serialize(payload, RecipeJsonContext.Default.RecipeListResult));
    }

    private CallToolResult Get(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var id = RequireString(args, "id");
        var recipe = _store.Find(id);
        return recipe is null ? NotFound(id) : Text(JsonSerializer.Serialize(recipe, RecipeJsonContext.Default.Recipe));
    }

    private CallToolResult Add(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var draft = new RecipeDraft
        {
            Title = GetString(args, "title"),
            When = GetString(args, "when"),
            Then = GetString(args, "then"),
            Services = GetStringList(args, "services"),
            Notes = GetString(args, "notes"),
        };
        var stored = _store.Add(draft, RecipeSources.Agent);
        return Text(JsonSerializer.Serialize(stored, RecipeJsonContext.Default.Recipe));
    }

    private CallToolResult Update(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var id = RequireString(args, "id");
        var existing = _store.Find(id);
        if (existing is null)
            return NotFound(id);

        var draft = RecipeDraft.From(existing);
        if (Has(args, "title")) draft.Title = GetString(args, "title");
        if (Has(args, "when")) draft.When = GetString(args, "when");
        if (Has(args, "then")) draft.Then = GetString(args, "then");
        if (Has(args, "services")) draft.Services = GetStringList(args, "services");
        if (Has(args, "notes")) draft.Notes = GetString(args, "notes");

        var updated = _store.Update(id, draft, RecipeSources.Agent);
        return updated is null ? NotFound(id) : Text(JsonSerializer.Serialize(updated, RecipeJsonContext.Default.Recipe));
    }

    private CallToolResult Remove(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var id = RequireString(args, "id");
        return _store.Remove(id) ? Text($"Removed recipe '{id}'.") : NotFound(id);
    }

    // ---- argument helpers -----------------------------------------------------------------------

    private static bool Has(IReadOnlyDictionary<string, JsonElement>? args, string name)
        => args is not null && args.TryGetValue(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? GetString(IReadOnlyDictionary<string, JsonElement>? args, string name)
    {
        if (!Has(args, name))
            return null;
        var v = args![name];
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => throw new RecipeValidationException($"'{name}' must be a string."),
        };
    }

    private static string RequireString(IReadOnlyDictionary<string, JsonElement>? args, string name)
    {
        var value = GetString(args, name)?.Trim();
        return string.IsNullOrEmpty(value) ? throw new RecipeValidationException($"'{name}' is required.") : value;
    }

    /// <summary>Accepts a JSON array of strings or a single comma-separated string.</summary>
    private static IReadOnlyList<string>? GetStringList(IReadOnlyDictionary<string, JsonElement>? args, string name)
    {
        if (!Has(args, name))
            return null;
        var v = args![name];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? string.Empty
                    : throw new RecipeValidationException($"'{name}' must be an array of strings."))
                .ToList(),
            JsonValueKind.String => (v.GetString() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => throw new RecipeValidationException($"'{name}' must be an array of strings."),
        };
    }

    // ---- result helpers -------------------------------------------------------------------------

    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult NotFound(string id) => Error($"No recipe with id '{id}'. Call recipes__list to see what exists.");

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
