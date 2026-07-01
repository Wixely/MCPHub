using MCPHub.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MCPHub.Tests;

public class LogStoreLoggerProviderTests
{
    [Theory]
    [InlineData("MCPHub.Proxy.UpstreamRegistry")]
    [InlineData("MCPHub.App.Proxy.ProxyCoordinator")]
    [InlineData("MCPHub.AppHost.ProxyHost")]
    public void Proxy_category_logs_are_captured_under_the_proxy_key(string category)
    {
        var store = new LogStore(capacity: 100);
        using var provider = new LogStoreLoggerProvider(store);

        provider.CreateLogger(category).LogInformation("Connected upstream Github.");

        var lines = store.Snapshot(LogStoreLoggerProvider.ProxyLogKey);
        Assert.Contains(lines, l => l.Text.Contains("Connected upstream Github."));
    }

    [Fact]
    public void Warnings_and_errors_are_captured_as_the_stderr_stream()
    {
        var store = new LogStore(capacity: 100);
        using var provider = new LogStoreLoggerProvider(store);

        provider.CreateLogger("MCPHub.Proxy.UpstreamRegistry").LogWarning("Upstream flaky.");

        var line = Assert.Single(store.Snapshot(LogStoreLoggerProvider.ProxyLogKey));
        Assert.Equal(LogStream.Stderr, line.Stream);
    }

    [Fact]
    public void Non_proxy_categories_are_ignored()
    {
        var store = new LogStore(capacity: 100);
        using var provider = new LogStoreLoggerProvider(store);

        provider.CreateLogger("MCPHub.Core.Services.Github.ReleaseService").LogInformation("Checked releases.");

        Assert.Empty(store.Snapshot(LogStoreLoggerProvider.ProxyLogKey));
    }
}
