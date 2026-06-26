using System.Text.Json.Nodes;
using MCPHub.Core.Services;
using Xunit;

namespace MCPHub.Tests;

public class ConfigMergeServiceTests
{
    private readonly ConfigMergeService _sut = new();

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void User_scalar_value_wins_over_default()
    {
        var merged = (JsonObject)_sut.Merge(
            Obj("""{ "Noteworthy": { "ReadOnly": false } }"""),
            Obj("""{ "Noteworthy": { "ReadOnly": true } }"""))!;

        Assert.False(merged["Noteworthy"]!["ReadOnly"]!.GetValue<bool>());
    }

    [Fact]
    public void New_default_key_is_added_while_user_value_is_kept()
    {
        var merged = (JsonObject)_sut.Merge(
            Obj("""{ "Server": { "Port": 5711 } }"""),
            Obj("""{ "Server": { "Port": 5710, "Path": "/mcp" } }"""))!;

        Assert.Equal(5711, merged["Server"]!["Port"]!.GetValue<int>());
        Assert.Equal("/mcp", merged["Server"]!["Path"]!.GetValue<string>());
    }

    [Fact]
    public void Key_removed_in_new_default_is_dropped()
    {
        var merged = (JsonObject)_sut.Merge(
            Obj("""{ "Server": { "Port": 5711, "Obsolete": "x" } }"""),
            Obj("""{ "Server": { "Port": 5710 } }"""))!;

        Assert.False(((JsonObject)merged["Server"]!).ContainsKey("Obsolete"));
    }

    [Fact]
    public void Nested_objects_merge_recursively()
    {
        var merged = (JsonObject)_sut.Merge(
            Obj("""{ "A": { "B": { "C": 1 } } }"""),
            Obj("""{ "A": { "B": { "C": 9, "D": 2 } } }"""))!;

        Assert.Equal(1, merged["A"]!["B"]!["C"]!.GetValue<int>()); // user wins
        Assert.Equal(2, merged["A"]!["B"]!["D"]!.GetValue<int>()); // new default added
    }

    [Fact]
    public void Array_is_taken_wholesale_from_user()
    {
        var merged = (JsonObject)_sut.Merge(
            Obj("""{ "Servers": [ "a", "b" ] }"""),
            Obj("""{ "Servers": [ "default" ] }"""))!;

        var arr = (JsonArray)merged["Servers"]!;
        Assert.Equal(2, arr.Count);
        Assert.Equal("a", arr[0]!.GetValue<string>());
    }

    [Fact]
    public void Type_change_resolves_to_the_default_shape()
    {
        var merged = (JsonObject)_sut.Merge(
            Obj("""{ "Thing": "was-a-scalar" }"""),
            Obj("""{ "Thing": { "Nested": 1 } }"""))!;

        Assert.True(merged["Thing"] is JsonObject);
        Assert.Equal(1, merged["Thing"]!["Nested"]!.GetValue<int>());
    }

    [Fact]
    public void MergeJson_with_no_existing_returns_default()
    {
        var node = JsonNode.Parse(_sut.MergeJson(null, """{ "A": 1 }"""))!;
        Assert.Equal(1, node["A"]!.GetValue<int>());
    }

    [Fact]
    public void MergeJson_with_corrupt_user_falls_back_to_default()
    {
        var node = JsonNode.Parse(_sut.MergeJson("{ not valid json", """{ "A": 1 }"""))!;
        Assert.Equal(1, node["A"]!.GetValue<int>());
    }

    [Fact]
    public void MergeJson_preserves_user_values_and_adds_new_keys()
    {
        const string user = """{ "Noteworthy": { "ReadOnly": false }, "Server": { "Port": 5711 } }""";
        const string def = """{ "Noteworthy": { "ReadOnly": true, "AllowDelete": false }, "Server": { "Port": 5710, "Path": "/mcp" } }""";

        var node = JsonNode.Parse(_sut.MergeJson(user, def))!;

        Assert.False(node["Noteworthy"]!["ReadOnly"]!.GetValue<bool>());    // user kept
        Assert.False(node["Noteworthy"]!["AllowDelete"]!.GetValue<bool>()); // new default added
        Assert.Equal(5711, node["Server"]!["Port"]!.GetValue<int>());       // user kept
        Assert.Equal("/mcp", node["Server"]!["Path"]!.GetValue<string>());  // new default added
    }
}
