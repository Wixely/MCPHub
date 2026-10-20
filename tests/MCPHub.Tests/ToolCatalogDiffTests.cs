using System.Text.Json;
using MCPHub.App.ViewModels;
using MCPHub.Proxy;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// What refreshing a server reports about its tools. The point is the operator noticing that an update to
/// a server they do not control added a tool, renamed one, or changed what a parameter accepts.
/// </summary>
public class ToolCatalogDiffTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Tool T(string name, string schema = """{"type":"object"}""", string? description = null, string? output = null, ToolAnnotations? annotations = null) => new()
    {
        Name = name,
        Description = description,
        InputSchema = Json(schema),
        OutputSchema = output is null ? null : Json(output),
        Annotations = annotations,
    };

    private const string Search = """
        {"type":"object","properties":{"query":{"type":"string","description":"What to find"},"limit":{"type":"integer","default":10}},"required":["query"]}
        """;

    [Fact]
    public void Identical_listings_report_nothing_even_when_json_members_are_reordered()
    {
        var reordered = """
            {"required":["query"],"properties":{"limit":{"default":10,"type":"integer"},"query":{"description":"What to find","type":"string"}},"type":"object"}
            """;

        var diff = ToolCatalogDiff.Compare([T("search", Search, "Finds things")], [T("search", reordered, "Finds things")]);

        Assert.False(diff.HasChanges);
        Assert.Equal(1, diff.UnchangedCount);
    }

    [Fact]
    public void A_new_tool_is_added_with_its_parameters_listed()
    {
        var diff = ToolCatalogDiff.Compare([T("search", Search)], [T("search", Search), T("fetch", """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""", "Fetches a page")]);

        var change = Assert.Single(diff.Changes);
        Assert.Equal(ToolChangeKind.Added, change.Kind);
        Assert.Equal("fetch", change.Name);
        Assert.Contains("Fetches a page", change.Details);
        Assert.Contains("Parameters: url (string, required).", change.Details);
    }

    [Fact]
    public void A_vanished_tool_is_removed()
    {
        var diff = ToolCatalogDiff.Compare([T("search", Search), T("ping")], [T("search", Search)]);

        var change = Assert.Single(diff.Changes);
        Assert.Equal(ToolChangeKind.Removed, change.Kind);
        Assert.Equal("ping", change.Name);
        Assert.Contains("No parameters.", change.Details);
    }

    [Fact]
    public void Same_parameters_under_a_new_name_reads_as_a_rename()
    {
        var diff = ToolCatalogDiff.Compare([T("search", Search, "Finds things")], [T("find", Search, "Finds things in the index")]);

        var change = Assert.Single(diff.Changes);
        Assert.Equal(ToolChangeKind.Renamed, change.Kind);
        Assert.Equal("find", change.Name);
        Assert.Equal("search", change.PreviousName);
        Assert.Contains(change.Details, d => d.Contains("Was \"search\""));
        Assert.Contains(change.Details, d => d.StartsWith("Description changed"));
    }

    [Fact]
    public void Tools_with_no_parameters_and_different_descriptions_are_not_paired_as_renames()
    {
        // Many tools take nothing; "both take nothing" is not evidence of anything.
        var diff = ToolCatalogDiff.Compare([T("ping", description: "Checks the server")], [T("version", description: "Reports the version")]);

        Assert.Equal([ToolChangeKind.Added, ToolChangeKind.Removed], diff.Changes.Select(c => c.Kind));
    }

    [Fact]
    public void An_ambiguous_rename_is_left_as_added_and_removed()
    {
        var diff = ToolCatalogDiff.Compare([T("search", Search)], [T("find", Search), T("lookup", Search)]);

        Assert.DoesNotContain(diff.Changes, c => c.Kind == ToolChangeKind.Renamed);
        Assert.Equal(2, diff.Count(ToolChangeKind.Added));
        Assert.Equal(1, diff.Count(ToolChangeKind.Removed));
    }

    [Fact]
    public void A_parameter_type_change_is_spelled_out()
    {
        var after = Search.Replace("\"limit\":{\"type\":\"integer\"", "\"limit\":{\"type\":\"string\"");

        var change = Assert.Single(ToolCatalogDiff.Compare([T("search", Search)], [T("search", after)]).Changes);

        Assert.Equal(ToolChangeKind.Changed, change.Kind);
        Assert.Contains("Parameter \"limit\" type changed from integer to string.", change.Details);
    }

    [Fact]
    public void Parameters_added_removed_and_made_required_are_each_reported()
    {
        var after = """
            {"type":"object","properties":{"query":{"type":"string","description":"What to find"},"limit":{"type":"integer","default":10},"cursor":{"type":["string","null"]}},"required":["query","limit"]}
            """;
        var fewer = """{"type":"object","properties":{"query":{"type":"string","description":"What to find"}},"required":["query"]}""";

        var grew = Assert.Single(ToolCatalogDiff.Compare([T("search", Search)], [T("search", after)]).Changes).Details;
        Assert.Contains("Parameter \"cursor\" added (string | null, optional).", grew);
        Assert.Contains("Parameter \"limit\" is now required.", grew);

        var shrank = Assert.Single(ToolCatalogDiff.Compare([T("search", Search)], [T("search", fewer)]).Changes).Details;
        Assert.Contains("Parameter \"limit\" removed.", shrank);
    }

    [Fact]
    public void Allowed_values_defaults_descriptions_and_constraints_are_reported()
    {
        var before = """{"type":"object","properties":{"mode":{"type":"string","enum":["fast","safe"],"default":"safe"},"n":{"type":"integer","minimum":1}}}""";
        var after = """{"type":"object","properties":{"mode":{"type":"string","enum":["fast","safe","exact"],"default":"fast","description":"How hard to try"},"n":{"type":"integer","minimum":0,"maximum":5}}}""";

        var details = Assert.Single(ToolCatalogDiff.Compare([T("run", before)], [T("run", after)]).Changes).Details;

        Assert.Contains("Parameter \"mode\" allowed values changed from [\"fast\",\"safe\"] to [\"fast\",\"safe\",\"exact\"].", details);
        Assert.Contains("Parameter \"mode\" default changed from \"safe\" to \"fast\".", details);
        Assert.Contains("Parameter \"mode\" description changed.", details);
        Assert.Contains("Parameter \"n\" constraints changed (maximum, minimum).", details);
    }

    [Fact]
    public void Nested_objects_and_array_items_are_followed_with_a_path()
    {
        var before = """{"type":"object","properties":{"filter":{"type":"object","properties":{"since":{"type":"string"}}},"ids":{"type":"array","items":{"type":"integer"}},"rows":{"type":"array","items":{"type":"object","properties":{"k":{"type":"string"}}}}}}""";
        var after = """{"type":"object","properties":{"filter":{"type":"object","properties":{"since":{"type":"integer"}}},"ids":{"type":"array","items":{"type":"string"}},"rows":{"type":"array","items":{"type":"object","properties":{"k":{"type":"string"},"v":{"type":"number"}}}}}}""";

        var details = Assert.Single(ToolCatalogDiff.Compare([T("q", before)], [T("q", after)]).Changes).Details;

        Assert.Contains("Parameter \"filter.since\" type changed from string to integer.", details);
        Assert.Contains("Parameter \"ids\" type changed from array of integer to array of string.", details);
        Assert.Contains("Parameter \"rows[].v\" added (number, optional).", details);
    }

    [Fact]
    public void Output_schema_and_annotation_hints_are_compared()
    {
        var before = T("del", annotations: new ToolAnnotations { ReadOnlyHint = true });
        var after = T("del", output: """{"type":"object","properties":{"deleted":{"type":"integer"}}}""",
            annotations: new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true });

        var details = Assert.Single(ToolCatalogDiff.Compare([before], [after]).Changes).Details;

        Assert.Contains("Now declares a structured result: deleted (integer).", details);
        Assert.Contains("Read-only hint changed from true to false.", details);
        Assert.Contains("Destructive hint changed from unset to true.", details);
    }

    [Fact]
    public void A_whitespace_only_description_change_is_not_a_change()
    {
        var diff = ToolCatalogDiff.Compare([T("a", description: "Does   a\nthing")], [T("a", description: "Does a thing")]);

        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Changes_come_back_added_then_renamed_then_changed_then_removed()
    {
        var diff = ToolCatalogDiff.Compare(
            [T("old_search", Search), T("tweak", description: "v1"), T("gone")],
            [T("new_search", Search), T("tweak", description: "v2"), T("fresh")]);

        Assert.Equal(
            [ToolChangeKind.Added, ToolChangeKind.Renamed, ToolChangeKind.Changed, ToolChangeKind.Removed],
            diff.Changes.Select(c => c.Kind));
    }

    [Fact]
    public void The_dialog_summary_counts_each_kind_and_flags_inferred_renames()
    {
        var diff = ToolCatalogDiff.Compare([T("old_search", Search), T("keep")], [T("new_search", Search), T("keep"), T("fresh")]);

        var page = ToolChangesViewModel.ForChanges("External", diff, wasConnected: true);

        Assert.Equal("Tools on External", page.Title);
        Assert.StartsWith("1 added, 1 renamed; 1 unchanged.", page.Summary);
        Assert.Contains("inferred", page.Note);
        Assert.Equal(["Added", "Renamed"], page.Rows.Select(r => r.KindText));
    }

    [Fact]
    public void The_dialog_says_when_there_is_nothing_to_compare_against()
    {
        var diff = ToolCatalogDiff.Compare([], [T("a"), T("b")]);

        var page = ToolChangesViewModel.ForChanges("External", diff, wasConnected: false);

        Assert.Contains("not connected before", page.Note);
        Assert.Equal(2, page.Rows.Count);
    }

    [Fact]
    public void The_dialog_says_plainly_when_nothing_changed_or_the_refresh_failed()
    {
        var same = ToolChangesViewModel.ForChanges("External", ToolCatalogDiff.Compare([T("a")], [T("a")]), wasConnected: true);
        Assert.Equal("No changes.", same.Summary);
        Assert.False(same.HasRows);
        Assert.False(same.HasNote);

        var failed = ToolChangesViewModel.ForFailure("External", "Connection refused");
        Assert.Contains("could not be reconnected", failed.Summary);
        Assert.Equal("Connection refused", failed.Note);
    }
}
