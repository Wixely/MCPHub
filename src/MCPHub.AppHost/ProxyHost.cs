using MCPHub.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPHub.AppHost;

/// <summary>
/// Owns the in-process Kestrel web application that exposes MCPHub's single aggregated MCP endpoint
/// (<c>/mcp</c>). Wires the dynamic proxy handlers into an <c>AddMcpServer().WithHttpTransport()</c>
/// host and binds to loopback only. Start/Stop/Restart let the UI control it without restarting the app.
/// </summary>
public sealed class ProxyHost
{
    private readonly IUpstreamRegistry _registry;
    private readonly ProxyHandlers _handlers;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    public ProxyHost(IUpstreamRegistry registry, ProxyHandlers handlers, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _handlers = handlers;
        _loggerFactory = loggerFactory;
    }

    public bool IsRunning => _app is not null;

    public string BindAddress { get; private set; } = "127.0.0.1";

    public int Port { get; private set; } = 5800;

    public string EndpointUrl => $"http://{BindAddress}:{Port}/mcp";

    /// <summary>Sets the bind address/port to use (ignored while running).</summary>
    public void Configure(string bindAddress, int port)
    {
        if (_app is not null)
            return;
        BindAddress = bindAddress;
        Port = port;
    }

    public async Task StartAsync(string bindAddress, int port, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null)
                return;

            BindAddress = bindAddress;
            Port = port;

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();

            builder.Services.AddMcpServer(options =>
                {
                    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                    {
                        Name = "MCPHub",
                        Version = "0.1.0",
                    };
                })
                .WithHttpTransport()
                .WithListToolsHandler(_handlers.ListToolsAsync)
                .WithCallToolHandler(_handlers.CallToolAsync);

            var app = builder.Build();
            app.Urls.Clear();
            app.Urls.Add($"http://{bindAddress}:{port}");
            app.MapMcp("/mcp");

            await app.StartAsync(cancellationToken);
            _app = app;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_app is null)
                return;

            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(string bindAddress, int port, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        await StartAsync(bindAddress, port, cancellationToken);
    }
}
