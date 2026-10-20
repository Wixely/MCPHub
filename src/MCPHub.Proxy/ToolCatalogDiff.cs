using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace MCPHub.Proxy;

/// <summary>What happened to one tool between two listings of the same server.</summary>
public enum ToolChangeKind
{
    /// <summary>A tool the server did not offer before.</summary>
    Added,

    /// <summary>
    /// A tool that disappeared under one name and appeared under another with the same parameters or the
    /// same description. A judgement, not a fact: servers do not say they renamed anything.
    /// </summary>
    Renamed,

    /// <summary>The same name, with something about it different.</summary>
    Changed,

    /// <summary>A tool the server no longer offers.</summary>
    Removed,
}

/// <summary>One tool's change, with the details worth reading.</summary>
/// <param name="Kind">What happened to the tool.</param>
/// <param name="Name">The tool's name now; for a removed tool, the name it had.</param>
/// <param name="PreviousName">For a rename, what it was called before.</param>
/// <param name="Details">Human-readable lines: a parameter added, a type changed, a hint flipped.</param>
public sealed record ToolChange(ToolChangeKind Kind, string Name, string? PreviousName, IReadOnlyList<string> Details);

/// <summary>Everything that differs between two listings of one server's tools.</summary>
/// <param name="Changes">Each tool that differs, added first and removed last.</param>
/// <param name="ToolCountBefore">How many tools the earlier listing had.</param>
/// <param name="ToolCountAfter">How many tools the later listing has.</param>
/// <param name="UnchangedCount">How many tools are identical in both.</param>
public sealed record ToolCatalogChanges(IReadOnlyList<ToolChange> Changes, int ToolCountBefore, int ToolCountAfter, int UnchangedCount)
{
    /// <summary>Whether anything at all differs.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>How many changes are of <paramref name="kind"/>.</summary>
    public int Count(ToolChangeKind kind) => Changes.Count(c => c.Kind == kind);
}

/// <summary>
/// Compares two listings of a server's tools, for showing what an update to that server changed. Works on
/// the tools as the server lists them, before the proxy namespaces them, so the names read as the server's
/// own. The comparison is semantic where JSON allows: property order never counts as a change.
/// </summary>
public static class ToolCatalogDiff
{
    private const int TextLimit = 140;

    /// <summary>Describes how <paramref name="after"/> differs from <paramref name="before"/>.</summary>
    public static ToolCatalogChanges Compare(IReadOnlyList<Tool> before, IReadOnlyList<Tool> after)
    {
        var old = ByName(before);
        var now = ByName(after);

        var removed = old.Keys.Where(n => !now.ContainsKey(n)).ToList();
        var added = now.Keys.Where(n => !old.ContainsKey(n)).ToList();
        var changes = new List<ToolChange>();
        var unchanged = 0;

        foreach (var (name, previous) in old)
        {
            if (!now.TryGetValue(name, out var current))
                continue;

            var details = DescribeChanges(previous, current);
            if (details.Count == 0)
                unchanged++;
            else
                changes.Add(new ToolChange(ToolChangeKind.Changed, name, null, details));
        }

        foreach (var (from, to) in PairRenames(removed, added, old, now))
        {
            removed.Remove(from);
            added.Remove(to);
            var details = new List<string> { $"Was \"{from}\"." };
            details.AddRange(DescribeChanges(old[from], now[to]));
            changes.Add(new ToolChange(ToolChangeKind.Renamed, to, from, details));
        }

        changes.AddRange(added.Select(n => new ToolChange(ToolChangeKind.Added, n, null, DescribeNew(now[n]))));
        changes.AddRange(removed.Select(n => new ToolChange(ToolChangeKind.Removed, n, null, DescribeParameters(old[n].InputSchema))));

        var ordered = changes
            .OrderBy(c => c.Kind)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ToolCatalogChanges(ordered, old.Count, now.Count, unchanged);
    }

    /// <summary>The differences between two versions of one tool, ignoring its name.</summary>
    public static IReadOnlyList<string> DescribeChanges(Tool before, Tool after)
    {
        var details = new List<string>();

        if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal))
            details.Add($"Title changed from {Quote(before.Title)} to {Quote(after.Title)}.");

        if (!string.Equals(Normalize(before.Description), Normalize(after.Description), StringComparison.Ordinal))
            details.Add($"Description changed from {Quote(before.Description)} to {Quote(after.Description)}.");

        var inputBefore = details.Count;
        CompareSchema(before.InputSchema, after.InputSchema, path: "", label: "Parameter", details);
        if (details.Count == inputBefore && !SemanticEquals(before.InputSchema, after.InputSchema))
            details.Add("The parameter schema changed in a way not broken down here.");

        CompareOutput(before.OutputSchema, after.OutputSchema, details);
        CompareAnnotations(before.Annotations, after.Annotations, details);

        // Whatever is left — execution settings, icons, _meta — still counts, if only as a line. The name and
        // description are left out: the name is the key being compared, and the description is compared above
        // with whitespace normalised, which a raw comparison here would undo.
        if (details.Count == 0 && !SemanticEquals(Serialize(before), Serialize(after), ignoring: ["name", "description"]))
            details.Add("Other tool metadata changed (execution settings, icons or _meta).");

        return details;
    }

    private static Dictionary<string, Tool> ByName(IReadOnlyList<Tool> tools)
    {
        var map = new Dictionary<string, Tool>(StringComparer.Ordinal);
        foreach (var tool in tools)
            map.TryAdd(tool.Name, tool); // a server listing one name twice: the first is what a caller gets
        return map;
    }

    /// <summary>
    /// Pairs a removed tool with an added one when they share their parameters (and have at least one, since
    /// many tools take none) or their description, and the match is unambiguous both ways.
    /// </summary>
    private static List<(string From, string To)> PairRenames(
        List<string> removed, List<string> added, Dictionary<string, Tool> old, Dictionary<string, Tool> now)
    {
        bool Alike(Tool a, Tool b)
        {
            var sameParameters = ParameterCount(a.InputSchema) > 0 && SemanticEquals(a.InputSchema, b.InputSchema);
            var sameDescription = !string.IsNullOrWhiteSpace(a.Description)
                                  && string.Equals(Normalize(a.Description), Normalize(b.Description), StringComparison.Ordinal);
            return sameParameters || sameDescription;
        }

        var pairs = new List<(string, string)>();
        foreach (var from in removed)
        {
            var candidates = added.Where(to => Alike(old[from], now[to])).ToList();
            if (candidates.Count != 1)
                continue;

            var to = candidates[0];
            var rivals = removed.Count(other => Alike(old[other], now[to]));
            if (rivals == 1)
                pairs.Add((from, to));
        }

        return pairs;
    }

    private static List<string> DescribeNew(Tool tool)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(tool.Description))
            details.Add(Truncate(Normalize(tool.Description)!));
        details.AddRange(DescribeParameters(tool.InputSchema));
        return details;
    }

    private static List<string> DescribeParameters(JsonElement schema)
    {
        var properties = Properties(schema);
        if (properties.Count == 0)
            return ["No parameters."];

        var required = Required(schema);
        var parts = properties.Select(p => $"{p.Key} ({TypeText(p.Value)}{(required.Contains(p.Key) ? ", required" : "")})");
        return ["Parameters: " + string.Join(", ", parts) + "."];
    }

    private static void CompareOutput(JsonElement? before, JsonElement? after, List<string> details)
    {
        var hadOutput = before is { ValueKind: JsonValueKind.Object };
        var hasOutput = after is { ValueKind: JsonValueKind.Object };

        if (!hadOutput && hasOutput)
            details.Add("Now declares a structured result: " + string.Join(", ", Properties(after!.Value).Select(p => $"{p.Key} ({TypeText(p.Value)})")) + ".");
        else if (hadOutput && !hasOutput)
            details.Add("No longer declares a structured result.");
        else if (hadOutput && hasOutput)
        {
            var count = details.Count;
            CompareSchema(before!.Value, after!.Value, path: "", label: "Result field", details);
            if (details.Count == count && !SemanticEquals(before.Value, after.Value))
                details.Add("The result schema changed in a way not broken down here.");
        }
    }

    private static void CompareAnnotations(ToolAnnotations? before, ToolAnnotations? after, List<string> details)
    {
        void Hint(string name, bool? was, bool? now)
        {
            if (was != now)
                details.Add($"{name} hint changed from {Show(was)} to {Show(now)}.");
        }

        Hint("Read-only", before?.ReadOnlyHint, after?.ReadOnlyHint);
        Hint("Destructive", before?.DestructiveHint, after?.DestructiveHint);
        Hint("Idempotent", before?.IdempotentHint, after?.IdempotentHint);
        Hint("Open-world", before?.OpenWorldHint, after?.OpenWorldHint);

        if (!string.Equals(before?.Title, after?.Title, StringComparison.Ordinal))
            details.Add($"Annotation title changed from {Quote(before?.Title)} to {Quote(after?.Title)}.");

        static string Show(bool? value) => value is null ? "unset" : value.Value ? "true" : "false";
    }

    /// <summary>
    /// Compares the members of two object schemas: which exist, which are required, and for each member in
    /// both, its type and constraints — recursing into nested objects and array items with a dotted path.
    /// </summary>
    private static void CompareSchema(JsonElement before, JsonElement after, string path, string label, List<string> details)
    {
        // Dictionaries for lookup; insertion order is kept, so lines come out in declaration order.
        var oldProps = Properties(before).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var newProps = Properties(after).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var oldRequired = Required(before);
        var newRequired = Required(after);

        foreach (var (name, schema) in newProps)
        {
            if (oldProps.ContainsKey(name))
                continue;
            var requirement = newRequired.Contains(name) ? "required" : "optional";
            details.Add($"{label} \"{path}{name}\" added ({TypeText(schema)}, {requirement}).");
        }

        foreach (var name in oldProps.Keys.Where(n => !newProps.ContainsKey(n)))
            details.Add($"{label} \"{path}{name}\" removed.");

        foreach (var (name, was) in oldProps)
        {
            if (!newProps.TryGetValue(name, out var now))
                continue;

            var at = $"{label} \"{path}{name}\"";
            var typeWas = TypeText(was);
            var typeNow = TypeText(now);
            if (!string.Equals(typeWas, typeNow, StringComparison.Ordinal))
                details.Add($"{at} type changed from {typeWas} to {typeNow}.");

            var wasRequired = oldRequired.Contains(name);
            var isRequired = newRequired.Contains(name);
            if (wasRequired != isRequired)
                details.Add(isRequired ? $"{at} is now required." : $"{at} is now optional.");

            if (!KeywordEquals(was, now, "enum"))
                details.Add($"{at} allowed values changed from {Short(was, "enum")} to {Short(now, "enum")}.");

            if (!KeywordEquals(was, now, "default"))
                details.Add($"{at} default changed from {Short(was, "default")} to {Short(now, "default")}.");

            if (!KeywordEquals(was, now, "description"))
                details.Add($"{at} description changed.");

            var constraints = ConstraintKeywords(was, now);
            if (constraints.Count > 0)
                details.Add($"{at} constraints changed ({string.Join(", ", constraints)}).");

            if (Properties(was).Count > 0 || Properties(now).Count > 0)
                CompareSchema(was, now, $"{path}{name}.", label, details);

            if (Member(was, "items") is { } itemsWas && Member(now, "items") is { } itemsNow)
                CompareSchema(itemsWas, itemsNow, $"{path}{name}[].", label, details);
        }
    }

    /// <summary>Keywords outside the ones described individually, whose values differ.</summary>
    private static List<string> ConstraintKeywords(JsonElement before, JsonElement after)
    {
        string[] handled = ["type", "enum", "default", "description", "properties", "required", "items", "title", "$schema"];
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var element in new[] { before, after })
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var property in element.EnumerateObject())
                if (!handled.Contains(property.Name))
                    names.Add(property.Name);
        }

        return names.Where(n => !KeywordEquals(before, after, n)).ToList();
    }

    /// <summary>A short, readable type: <c>string</c>, <c>integer | null</c>, <c>array of string</c>.</summary>
    internal static string TypeText(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return "any";

        if (Member(schema, "type") is { } type)
        {
            var names = type.ValueKind switch
            {
                JsonValueKind.String => [type.GetString()!],
                JsonValueKind.Array => type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList(),
                _ => new List<string>(),
            };

            if (names.Count > 0)
            {
                var items = Member(schema, "items");
                return string.Join(" | ", names.Select(n =>
                    n == "array" && items is { } i ? $"array of {TypeText(i)}" : n));
            }
        }

        if (Member(schema, "$ref") is { ValueKind: JsonValueKind.String } reference)
            return reference.GetString()!.Split('/').Last();

        foreach (var combinator in new[] { "anyOf", "oneOf" })
        {
            if (Member(schema, combinator) is { ValueKind: JsonValueKind.Array } options)
                return string.Join(" | ", options.EnumerateArray().Select(TypeText).Distinct());
        }

        if (Member(schema, "enum") is not null)
            return "enum";
        if (Member(schema, "const") is not null)
            return "const";
        return "any";
    }

    private static int ParameterCount(JsonElement schema) => Properties(schema).Count;

    private static List<KeyValuePair<string, JsonElement>> Properties(JsonElement schema)
    {
        if (Member(schema, "properties") is not { ValueKind: JsonValueKind.Object } properties)
            return [];
        return properties.EnumerateObject().Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value)).ToList();
    }

    private static HashSet<string> Required(JsonElement schema)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (Member(schema, "required") is { ValueKind: JsonValueKind.Array } required)
        {
            foreach (var item in required.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    set.Add(item.GetString()!);
        }

        return set;
    }

    private static JsonElement? Member(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static bool KeywordEquals(JsonElement before, JsonElement after, string keyword)
    {
        var a = Member(before, keyword);
        var b = Member(after, keyword);
        if (a is null || b is null)
            return a is null && b is null;
        return SemanticEquals(a.Value, b.Value);
    }

    /// <summary>JSON equality that ignores the order of object members, which carries no meaning.</summary>
    internal static bool SemanticEquals(JsonElement a, JsonElement b, IReadOnlyCollection<string>? ignoring = null)
    {
        if (a.ValueKind != b.ValueKind)
            return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                // "ignoring" applies to this level only: the tool's own name, not a parameter called "name".
                var left = a.EnumerateObject().Where(p => ignoring?.Contains(p.Name) != true).ToList();
                var right = b.EnumerateObject().Where(p => ignoring?.Contains(p.Name) != true).ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                return left.Count == right.Count
                       && left.All(p => right.TryGetValue(p.Name, out var other) && SemanticEquals(p.Value, other));
            case JsonValueKind.Array:
                var x = a.EnumerateArray().ToList();
                var y = b.EnumerateArray().ToList();
                return x.Count == y.Count && x.Zip(y).All(pair => SemanticEquals(pair.First, pair.Second));
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                return a.TryGetDecimal(out var m) && b.TryGetDecimal(out var n) ? m == n : a.GetRawText() == b.GetRawText();
            default:
                return true; // true, false, null: equal kinds are equal values
        }
    }

    private static JsonElement Serialize(Tool tool) => JsonSerializer.SerializeToElement(tool, McpJsonUtilities.DefaultOptions);

    private static string Short(JsonElement schema, string keyword)
        => Member(schema, keyword) is { } value ? Truncate(value.GetRawText(), 60) : "none";

    private static string Quote(string? text)
        => string.IsNullOrWhiteSpace(text) ? "nothing" : "\"" + Truncate(Normalize(text)!) + "\"";

    private static string? Normalize(string? text)
        => text is null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string text, int limit = TextLimit)
        => text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";
}
