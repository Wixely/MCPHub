using MCPHub.Core.Logging;
using Xunit;

namespace MCPHub.Tests;

public class LogStoreTests
{
    private static LogLine Line(string text, LogStream stream = LogStream.Stdout)
        => new(DateTimeOffset.Now, stream, text);

    [Fact]
    public void Append_and_snapshot_preserve_order()
    {
        var store = new LogStore(capacity: 10);
        store.Append("svc", Line("a"));
        store.Append("svc", Line("b"));

        Assert.Equal(["a", "b"], store.Snapshot("svc").Select(l => l.Text));
    }

    [Fact]
    public void Trims_to_capacity_dropping_oldest()
    {
        var store = new LogStore(capacity: 3);
        foreach (var t in new[] { "1", "2", "3", "4", "5" })
            store.Append("svc", Line(t));

        var snapshot = store.Snapshot("svc");
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(["3", "4", "5"], snapshot.Select(l => l.Text));
    }

    [Fact]
    public void LineAppended_event_fires_with_service_and_line()
    {
        var store = new LogStore();
        string? service = null;
        LogLine? line = null;
        store.LineAppended += (s, l) => { service = s; line = l; };

        store.Append("svc", Line("hello"));

        Assert.Equal("svc", service);
        Assert.Equal("hello", line!.Text);
    }

    [Fact]
    public void Buffers_are_isolated_per_service()
    {
        var store = new LogStore();
        store.Append("a", Line("x"));
        store.Append("b", Line("y"));

        Assert.Equal("x", Assert.Single(store.Snapshot("a")).Text);
        Assert.Equal("y", Assert.Single(store.Snapshot("b")).Text);
    }

    [Fact]
    public void Clear_empties_one_service()
    {
        var store = new LogStore();
        store.Append("a", Line("x"));
        store.Clear("a");

        Assert.Empty(store.Snapshot("a"));
    }

    [Fact]
    public void Services_lists_only_keys_that_have_produced_output()
    {
        var store = new LogStore();
        Assert.Empty(store.Services);

        store.Append("a", Line("x"));

        Assert.Contains("a", store.Services);
        Assert.DoesNotContain("b", store.Services);
    }

    [Fact]
    public void Clear_drops_the_service_from_Services()
    {
        var store = new LogStore();
        store.Append("a", Line("x"));
        store.Clear("a");

        Assert.DoesNotContain("a", store.Services);
    }
}
