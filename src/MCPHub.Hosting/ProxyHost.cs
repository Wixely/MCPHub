using System.Security.Claims;
using MCPHub.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPHub.Hosting;

/// <summary>Options for <see cref="ProxyHost"/> beyond bind address and port.</summary>
public sealed class ProxyHostOptions
{
    /// <summary>
    /// When set, every HTTP request must present a <c>Bearer</c> token this authenticator accepts;
    /// unauthenticated requests get <c>401</c>. The resolved tenant is stamped onto the request
    /// principal as a <see cref="ProxyClaimTypes.TenantId"/> claim, which
    /// <see cref="ClaimsTenantResolver"/> (pass it to <see cref="ProxyHandlers"/>) turns back into
    /// the per-call <see cref="TenantContext"/>. When <see langword="null"/> (the default) the
    /// endpoint is anonymous and every call runs as <see cref="TenantContext.Default"/> —
    /// the desktop behavior.
    /// </summary>
    public ITenantAuthenticator? TenantAuthenticator { get; init; }

    /// <summary>Server name advertised in the MCP initialize handshake.</summary>
    public string ServerName { get; init; } = "MCPHub";

    /// <summary>Server version advertised in the MCP initialize handshake.</summary>
    public string ServerVersion { get; init; } =
        typeof(ProxyHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

/// <summary>
/// Owns the in-process Kestrel web application that exposes one aggregated MCP endpoint
/// (<c>/mcp</c>). Wires the dynamic proxy handlers into an <c>AddMcpServer().WithHttpTransport()</c>
/// host and binds to loopback by default. Start/Stop/Restart let the owner control it without
/// restarting the process; multiple hosts can run side by side in one process (e.g. one per tenant).
/// <para>
/// Tenant flow: bearer authentication runs as ASP.NET middleware and stamps the request principal;
/// the MCP transport carries each request's <see cref="ClaimsPrincipal"/> to the handler's
/// <c>RequestContext.User</c>, where an <see cref="ITenantResolver"/> maps it to a
/// <see cref="TenantContext"/> — so one host serves many tenants over one endpoint.
/// </para>
/// </summary>
public sealed class ProxyHost
{
    private readonly ProxyHandlers _handlers;
    private readonly ProxyHostOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;

    /// <summary>Creates a host around the given handlers; nothing binds until <see cref="StartAsync"/>.</summary>
    public ProxyHost(ProxyHandlers handlers, ILoggerFactory loggerFactory, ProxyHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _handlers = handlers;
        _options = options ?? new ProxyHostOptions();
        _logger = loggerFactory.CreateLogger<ProxyHost>();
    }

    /// <summary>Whether the web application is currently started.</summary>
    public bool IsRunning => _app is not null;

    /// <summary>Address the endpoint binds to (loopback by default).</summary>
    public string BindAddress { get; private set; } = "127.0.0.1";

    /// <summary>Listen port. Pass <c>0</c> to let the OS pick; reflects the actual port once started.</summary>
    public int Port { get; private set; } = 5800;

    /// <summary>Full URL of the aggregated MCP endpoint, e.g. <c>http://127.0.0.1:5800/mcp</c>.</summary>
    public string EndpointUrl => $"http://{BindAddress}:{Port}/mcp";

    /// <summary>Sets the bind address/port to use (ignored while running).</summary>
    public void Configure(string bindAddress, int port)
    {
        if (_app is not null)
            return;
        BindAddress = bindAddress;
        Port = port;
    }

    /// <summary>Starts the endpoint on the given address/port (no-op if already running). Port 0 lets the OS pick.</summary>
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
                        Name = _options.ServerName,
                        Version = _options.ServerVersion,
                    };
                })
                .WithHttpTransport()
                .WithListToolsHandler(_handlers.ListToolsAsync)
                .WithCallToolHandler(_handlers.CallToolAsync);

            var app = builder.Build();
            app.Urls.Clear();
            app.Urls.Add($"http://{bindAddress}:{port}");

            if (_options.TenantAuthenticator is { } authenticator)
                app.Use((context, next) => AuthenticateAsync(context, next, authenticator));

            app.MapMcp("/mcp");

            await app.StartAsync(cancellationToken);
            _app = app;
            ResolveBoundPort(app);
            _logger.LogInformation("Proxy started at {Endpoint}.", EndpointUrl);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops and disposes the web application (no-op if not running).</summary>
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
            _logger.LogInformation("Proxy stopped.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops (if running) and starts again on the given address/port.</summary>
    public async Task RestartAsync(string bindAddress, int port, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        await StartAsync(bindAddress, port, cancellationToken);
    }

    private static async Task AuthenticateAsync(HttpContext context, RequestDelegate next, ITenantAuthenticator authenticator)
    {
        TenantContext? tenant = null;
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            header["Bearer ".Length..].Trim() is { Length: > 0 } token)
        {
            tenant = await authenticator.AuthenticateAsync(token, context.RequestAborted);
        }

        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ProxyClaimTypes.TenantId, tenant.TenantId)],
            authenticationType: "Bearer"));
        await next(context);
    }

    /// <summary>When started with port 0, reflect the port Kestrel actually bound.</summary>
    private void ResolveBoundPort(WebApplication app)
    {
        if (Port != 0)
            return;

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri))
            Port = uri.Port;
    }
}
