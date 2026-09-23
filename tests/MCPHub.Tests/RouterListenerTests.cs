using System.Net;
using System.Net.Http.Headers;
using MCPHub.Core.Infrastructure;
using MCPHub.Core.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCPHub.Tests;

/// <summary>The configurable Router bind address, and applying listener changes without restarting MCPHub.</summary>
public sealed class RouterListenerTests
{
    [Fact]
    public void Bind_address_persists_and_only_accepts_ip_literals()
    {
        using var fixture = new RouterFixture();
        Assert.Equal("127.0.0.1", fixture.Store.Snapshot.BindAddress);

        fixture.Store.Configure("0.0.0.0", 5801, false);
        Assert.Equal("0.0.0.0", new RouterStore(fixture).Snapshot.BindAddress);

        // A host name would have to be resolved, and which answer it binds would be silently arbitrary.
        Assert.Throws<ArgumentException>(() => fixture.Store.Configure("localhost", 5801, false));
        Assert.Throws<ArgumentException>(() => fixture.Store.Configure("  ", 5801, false));
        Assert.Throws<ArgumentException>(() => fixture.Store.Configure("999.1.1.1", 5801, false));

        // The rejected values must not have been written over the good one.
        Assert.Equal("0.0.0.0", fixture.Store.Snapshot.BindAddress);
    }

    [Fact]
    public async Task Endpoint_url_is_dialable_even_when_bound_to_every_interface()
    {
        using var fixture = new RouterFixture();
        fixture.Store.Configure("0.0.0.0", 5801, false);
        await using var host = NewHost(fixture);

        Assert.True(host.IsBoundToAllInterfaces);
        // 0.0.0.0 is not an address anything can connect to, so the shown URL falls back to loopback.
        Assert.Equal("http://127.0.0.1:5801/v1", host.EndpointUrl);
    }

    [Fact]
    public async Task Applying_a_new_port_rebinds_the_running_listener_and_keeps_every_route()
    {
        using var fixture = new RouterFixture();
        await using var upstream = await MockUpstream.StartAsync();
        var output = fixture.Store.SaveOutput(null, "Mock", upstream.Url, null, null);
        fixture.Store.SetDefault(output);
        var agent = fixture.Store.AddInput("Agent", null);

        await using var host = NewHost(fixture);
        await host.StartAsync(portOverride: 0);
        var firstPort = host.Port;
        Assert.True(await CanReachAsync(host, agent.Key));

        // Pick a second free port by briefly binding one, then apply it while the Router is serving.
        var secondPort = FreePort();
        var rebound = await host.ApplyListenerAsync(() => fixture.Store.Configure("127.0.0.1", secondPort, false));

        Assert.True(rebound);
        Assert.True(host.IsRunning);
        Assert.Equal(secondPort, host.Port);
        Assert.NotEqual(firstPort, host.Port);
        // No MCPHub restart: the same agent key still routes to the same output on the new socket.
        Assert.True(await CanReachAsync(host, agent.Key));
    }

    [Fact]
    public async Task Applying_an_unbindable_port_restores_the_previous_listener_and_settings()
    {
        using var fixture = new RouterFixture();
        await using var occupied = await MockUpstream.StartAsync();
        var takenPort = new Uri(occupied.Url).Port;

        await using var host = NewHost(fixture);
        await host.StartAsync(portOverride: 0);
        var original = host.Port;
        fixture.Store.Configure("127.0.0.1", original, false);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            host.ApplyListenerAsync(() => fixture.Store.Configure("127.0.0.1", takenPort, false)));

        // A typo must not leave agents with no Router at all.
        Assert.True(host.IsRunning);
        Assert.Equal(original, host.Port);
        Assert.Equal(original, fixture.Store.Snapshot.Port);
    }

    [Fact]
    public async Task Applying_unchanged_settings_leaves_the_listener_alone()
    {
        using var fixture = new RouterFixture();
        await using var host = NewHost(fixture);
        await host.StartAsync(portOverride: 0);
        var port = host.Port;
        fixture.Store.Configure("127.0.0.1", port, false);

        var rebound = await host.ApplyListenerAsync(() => fixture.Store.Configure("127.0.0.1", port, true));

        Assert.False(rebound);
        Assert.Equal(port, host.Port);
        Assert.True(fixture.Store.Snapshot.StartOnLaunch);
    }

    [Fact]
    public async Task A_pinned_host_override_wins_over_the_configured_address()
    {
        using var fixture = new RouterFixture();
        fixture.Store.Configure("0.0.0.0", 5801, false);
        await using var host = new RouterHost(fixture.Store, NullLogger<RouterHost>.Instance, new() { BindAddress = "127.0.0.1" });

        Assert.Equal("127.0.0.1", host.ConfiguredBindAddress);
        await host.StartAsync(portOverride: 0);
        Assert.Equal("127.0.0.1", host.BindAddress);
    }

    private static RouterHost NewHost(RouterFixture fixture) =>
        new(fixture.Store, NullLogger<RouterHost>.Instance, activity: fixture.Activity);

    private static async Task<bool> CanReachAsync(RouterHost host, string key)
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/v1/"), Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.GetAsync("models");
        return response.IsSuccessStatusCode;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal sealed class RouterFixture : IAppPaths, IDisposable
    {
        public string SettingsDirectory { get; } = Path.Combine(Path.GetTempPath(), "mcphub-router-listener-tests", Guid.NewGuid().ToString("N"));
        public string DataDirectory => SettingsDirectory;
        public string DownloadsDirectory => SettingsDirectory;
        public string DefaultServersDirectory => SettingsDirectory;
        public string EnsureDirectory(string path) { Directory.CreateDirectory(path); return path; }
        public RouterStore Store { get; }
        public RouterActivityLog Activity { get; }

        public RouterFixture()
        {
            Directory.CreateDirectory(SettingsDirectory);
            Store = new(this);
            Activity = new(this, writeInterval: TimeSpan.Zero);
        }

        public void Dispose()
        {
            Activity.Dispose();
            try { Directory.Delete(SettingsDirectory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A stand-in OpenAI-compatible provider on a loopback port the OS picks.</summary>
    internal sealed class MockUpstream(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public static Task<MockUpstream> StartAsync(string body = """{"object":"list","data":[]}""") =>
            StartAsync(context =>
            {
                context.Response.ContentType = "application/json";
                return context.Response.WriteAsync(body);
            });

        public static async Task<MockUpstream> StartAsync(RequestDelegate handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            app.Run(handler);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, address.TrimEnd('/') + "/v1");
        }

        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
