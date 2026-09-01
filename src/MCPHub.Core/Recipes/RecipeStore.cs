using System.Text.Json;
using MCPHub.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Recipes;

/// <summary>
/// The recipes knowledge base: persistent, thread-safe, and shared by the UI page and the agent-facing
/// tools. Every mutation validates, writes <c>recipes.json</c> atomically, then raises <see cref="Changed"/>.
/// </summary>
public interface IRecipeStore
{
    /// <summary>Full path of the backing <c>recipes.json</c>.</summary>
    string FilePath { get; }

    /// <summary>Snapshot of every recipe, ordered by title.</summary>
    IReadOnlyList<Recipe> All { get; }

    /// <summary>Raised after any add / update / remove (on the mutating thread).</summary>
    event Action? Changed;

    /// <summary>Snapshot of one recipe by id (case-insensitive), or <see langword="null"/>.</summary>
    Recipe? Find(string id);

    /// <summary>
    /// Recipes whose title / when / then / notes / services contain <paramref name="query"/> (case-insensitive)
    /// and, when <paramref name="service"/> is given, that list that service key. Both filters optional.
    /// </summary>
    IReadOnlyList<Recipe> Search(string? query, string? service = null);

    /// <summary>Validates and stores a new recipe. Throws <see cref="RecipeValidationException"/> on bad input.</summary>
    Recipe Add(RecipeDraft draft, string source);

    /// <summary>
    /// Validates and replaces the fields of an existing recipe. Returns <see langword="null"/> when no recipe
    /// has that id. Throws <see cref="RecipeValidationException"/> on bad input.
    /// </summary>
    Recipe? Update(string id, RecipeDraft draft, string source);

    /// <summary>Removes a recipe; false when no recipe has that id.</summary>
    bool Remove(string id);
}

/// <inheritdoc />
public sealed class RecipeStore : IRecipeStore
{
    /// <summary>File name beside <c>settings.json</c> in the per-user settings directory.</summary>
    public const string FileName = "recipes.json";

    private readonly ILogger<RecipeStore> _logger;
    private readonly object _gate = new();
    private readonly List<Recipe> _recipes;

    public RecipeStore(IAppPaths appPaths, ILogger<RecipeStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appPaths.SettingsDirectory);
        FilePath = Path.Combine(appPaths.SettingsDirectory, FileName);
        _recipes = Load();
    }

    /// <inheritdoc />
    public string FilePath { get; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public IReadOnlyList<Recipe> All
    {
        get
        {
            lock (_gate)
                return Sorted(_recipes).Select(r => r.Clone()).ToList();
        }
    }

    /// <inheritdoc />
    public Recipe? Find(string id)
    {
        lock (_gate)
            return FindUnlocked(id)?.Clone();
    }

    /// <inheritdoc />
    public IReadOnlyList<Recipe> Search(string? query, string? service = null)
    {
        var q = query?.Trim();
        var s = RecipeValidator.NormalizeServiceKey(service);

        lock (_gate)
        {
            IEnumerable<Recipe> matches = _recipes;
            if (!string.IsNullOrEmpty(s))
                matches = matches.Where(r => r.Services.Contains(s, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(q))
                matches = matches.Where(r => Matches(r, q));
            return Sorted(matches).Select(r => r.Clone()).ToList();
        }
    }

    /// <inheritdoc />
    public Recipe Add(RecipeDraft draft, string source)
    {
        var validated = RecipeValidator.Validate(draft);
        var now = DateTimeOffset.UtcNow;
        Recipe stored;

        lock (_gate)
        {
            stored = new Recipe
            {
                Id = NewIdUnlocked(),
                Title = validated.Title,
                When = validated.When,
                Then = validated.Then,
                Services = [.. validated.Services],
                Notes = validated.Notes,
                Source = NormalizeSource(source),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _recipes.Add(stored);
            SaveUnlocked();
            stored = stored.Clone();
        }

        Changed?.Invoke();
        return stored;
    }

    /// <inheritdoc />
    public Recipe? Update(string id, RecipeDraft draft, string source)
    {
        var validated = RecipeValidator.Validate(draft);
        Recipe? updated;

        lock (_gate)
        {
            var existing = FindUnlocked(id);
            if (existing is null)
                return null;

            existing.Title = validated.Title;
            existing.When = validated.When;
            existing.Then = validated.Then;
            existing.Services = [.. validated.Services];
            existing.Notes = validated.Notes;
            existing.Source = NormalizeSource(source);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            SaveUnlocked();
            updated = existing.Clone();
        }

        Changed?.Invoke();
        return updated;
    }

    /// <inheritdoc />
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var existing = FindUnlocked(id);
            if (existing is null)
                return false;
            _recipes.Remove(existing);
            SaveUnlocked();
        }

        Changed?.Invoke();
        return true;
    }

    private Recipe? FindUnlocked(string id)
    {
        var trimmed = id?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : _recipes.FirstOrDefault(r => string.Equals(r.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private string NewIdUnlocked()
    {
        while (true)
        {
            var candidate = Guid.NewGuid().ToString("N")[..8];
            if (FindUnlocked(candidate) is null)
                return candidate;
        }
    }

    private static string NormalizeSource(string source)
        => string.Equals(source?.Trim(), RecipeSources.Agent, StringComparison.OrdinalIgnoreCase) ? RecipeSources.Agent : RecipeSources.User;

    private static IEnumerable<Recipe> Sorted(IEnumerable<Recipe> recipes)
        => recipes.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id, StringComparer.Ordinal);

    private static bool Matches(Recipe r, string q)
        => r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
           || r.When.Contains(q, StringComparison.OrdinalIgnoreCase)
           || r.Then.Contains(q, StringComparison.OrdinalIgnoreCase)
           || (r.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
           || r.Services.Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase));

    private void SaveUnlocked()
    {
        try
        {
            var file = new RecipeFile { Recipes = Sorted(_recipes).ToList() };
            var json = JsonSerializer.Serialize(file, RecipeJsonContext.Default.RecipeFile);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to save recipes to {Path}.", FilePath);
        }
    }

    private List<Recipe> Load()
    {
        if (!File.Exists(FilePath))
            return [];

        try
        {
            var json = File.ReadAllText(FilePath);
            var file = JsonSerializer.Deserialize(json, RecipeJsonContext.Default.RecipeFile);
            var recipes = file?.Recipes ?? [];

            // Never let a hand-edited file smuggle in an unusable entry: drop the broken ones, keep the rest.
            var kept = recipes.Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Title)).ToList();
            if (kept.Count != recipes.Count)
                _logger.LogWarning("Ignored {Count} recipe(s) without an id or title in {Path}.", recipes.Count - kept.Count, FilePath);
            foreach (var r in kept)
                r.Services = RecipeValidator.NormalizeServices(r.Services);
            return kept;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Keep the unreadable file for the user to inspect rather than overwriting it on the next save.
            var quarantine = $"{FilePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try { File.Move(FilePath, quarantine, overwrite: true); } catch (IOException) { /* best effort */ }
            _logger.LogWarning(ex, "Could not read recipes; moved the file to {Quarantine} and starting empty.", quarantine);
            return [];
        }
    }
}

/// <summary>
/// Normalises and bounds a <see cref="RecipeDraft"/>. Recipes are meant to be terse "if X then Y" notes,
/// so the limits are deliberately tight — a wall of text is a sign the knowledge belongs somewhere else.
/// </summary>
public static class RecipeValidator
{
    public const int MaxTitleLength = 80;
    public const int MaxWhenLength = 400;
    public const int MaxThenLength = 800;
    public const int MaxNotesLength = 1000;
    public const int MaxServices = 12;
    public const int MaxServiceKeyLength = 40;

    /// <summary>The validated, normalised fields.</summary>
    public sealed record Validated(string Title, string When, string Then, IReadOnlyList<string> Services, string? Notes);

    /// <summary>Validates <paramref name="draft"/>, throwing <see cref="RecipeValidationException"/> with a clear message.</summary>
    public static Validated Validate(RecipeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var title = Required(draft.Title, "title", MaxTitleLength, singleLine: true);
        var when = Required(draft.When, "when", MaxWhenLength, singleLine: false);
        var then = Required(draft.Then, "then", MaxThenLength, singleLine: false);

        var notes = draft.Notes?.Trim();
        if (string.IsNullOrEmpty(notes))
            notes = null;
        else if (notes.Length > MaxNotesLength)
            throw new RecipeValidationException($"'notes' is too long ({notes.Length} chars; max {MaxNotesLength}).");

        var services = NormalizeServices(draft.Services);
        if (services.Count > MaxServices)
            throw new RecipeValidationException($"Too many services ({services.Count}; max {MaxServices}).");
        foreach (var key in services)
        {
            if (key.Length > MaxServiceKeyLength)
                throw new RecipeValidationException($"Service key '{key}' is too long (max {MaxServiceKeyLength}).");
        }

        return new Validated(title, when, then, services, notes);
    }

    /// <summary>Lower-cases, trims and de-duplicates service keys; blanks are dropped.</summary>
    public static List<string> NormalizeServices(IEnumerable<string>? services)
        => (services ?? [])
            .Select(NormalizeServiceKey)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Lower-cases and trims one service key; <see langword="null"/>/blank becomes an empty string.</summary>
    public static string NormalizeServiceKey(string? key) => (key ?? string.Empty).Trim().ToLowerInvariant();

    private static string Required(string? value, string field, int max, bool singleLine)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new RecipeValidationException($"'{field}' is required.");
        if (singleLine)
            trimmed = string.Join(' ', trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (trimmed.Length > max)
            throw new RecipeValidationException($"'{field}' is too long ({trimmed.Length} chars; max {max}). Keep recipes concise.");
        return trimmed;
    }
}
