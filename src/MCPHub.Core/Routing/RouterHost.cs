using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MCPHub.Core.Routing;

/// <summary>Local OpenAI-compatible HTTP router. Every request captures one authenticated route.</summary>
public sealed class RouterHost : IAsyncDisposable
{
    public const int MaxRequestBytes = 16 * 1024 * 1024;
    private readonly IRouterConfigurationSource _configuration;
    private readonly IPAddress _bindAddress;
    private readonly ILogger<RouterHost> _logger;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _requests = new(32, 32);
    private WebApplication? _app;
    private CancellationTokenSource? _shutdown;

    public RouterHost(IRouterConfigurationSource configuration, ILogger<RouterHost> logger, RouterHostOptions? options = null)
    {
        _configuration = configuration;
        _logger = logger;
        if (!IPAddress.TryParse(options?.BindAddress ?? "127.0.0.1", out var bindAddress))
            throw new ArgumentException("Router bind address must be an IPv4 or IPv6 address.");
        _bindAddress = bindAddress;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }
    public string BindAddress => _bindAddress.ToString();
    public string EndpointUrl => new UriBuilder("http", BindAddress, IsRunning ? Port : _configuration.Snapshot.Port, "v1").Uri.AbsoluteUri.TrimEnd('/');
    public string? LastError { get; private set; }

    public async Task StartConfiguredAsync()
    {
        if (!_configuration.Snapshot.StartOnLaunch || _configuration.LoadError is not null) return;
        try { await StartAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            LastError = "Router could not start. Check the configured port and try again.";
            _logger.LogWarning("Model router could not start. Check its port configuration.");
        }
    }

    /// <summary>A zero override is reserved for isolated integration tests with an OS-assigned port.</summary>
    public async Task StartAsync(int? portOverride = null, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            if (_configuration.LoadError is not null) throw new InvalidOperationException(_configuration.LoadError);
            var port = portOverride ?? _configuration.Snapshot.Port;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders(); // Never log URLs, payloads, or credentials from routed requests.
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(_bindAddress, port);
                options.Limits.MaxRequestBodySize = MaxRequestBytes;
                options.Limits.MaxConcurrentConnections = 64;
            });
            var app = builder.Build();
            var shutdown = new CancellationTokenSource();
            app.Run(context => HandleAsync(context, shutdown.Token));
            try { await app.StartAsync(cancellationToken).ConfigureAwait(false); }
            catch
            {
                shutdown.Dispose();
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            _shutdown = shutdown;
            _app = app;
            LastError = null;
            _logger.LogInformation("Model router started on port {Port}.", Port);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_app is null) return;
            await _shutdown!.CancelAsync().ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
            finally
            {
                await _app.DisposeAsync().ConfigureAwait(false);
                _app = null;
                _shutdown.Dispose();
                _shutdown = null;
            }
            _logger.LogInformation("Model router stopped.");
        }
        finally { _lifecycle.Release(); }
    }

    private async Task HandleAsync(HttpContext context, CancellationToken shutdown)
    {
        var header = context.Request.Headers.Authorization;
        RouterRoute? route = null;
        if (header.Count == 1 && AuthenticationHeaderValue.TryParse(header[0], out var auth) &&
            auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && auth.Parameter is { } key)
            route = _configuration.Resolve(key);
        if (route is null)
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await ErrorAsync(context, 401, "invalid_api_key", "A valid enabled Router input key is required.");
            return;
        }

        var path = context.Request.Path.Value;
        var allowed = HttpMethods.IsGet(context.Request.Method) && path == "/v1/models" ||
            HttpMethods.IsPost(context.Request.Method) && path is "/v1/chat/completions" or "/v1/responses" or "/v1/completions" or "/v1/embeddings";
        if (!allowed || context.WebSockets.IsWebSocketRequest || context.Request.QueryString.HasValue)
        {
            await ErrorAsync(context, 404, "unsupported_endpoint", "This model API endpoint or request mode is not supported by the Router.");
            return;
        }
        if (route.Output is not { } output)
        {
            await ErrorAsync(context, 503, "route_unavailable", "No model output is assigned to this input. Set a global default or input override.");
            return;
        }
        var target = new Uri(new Uri(RouterConfigurationRules.NormalizeBaseUrl(output.BaseUrl)), path![4..]);
        if (target.IsLoopback && target.Port == Port)
        {
            await ErrorAsync(context, 502, "routing_loop", "The selected output points back to this Router.");
            return;
        }
        if (!await _requests.WaitAsync(0, context.RequestAborted))
        {
            context.Response.Headers.RetryAfter = "1";
            await ErrorAsync(context, 429, "router_busy", "Router is at its concurrent request limit. Retry shortly.");
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, shutdown);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
            // Allowlist headers; input credentials, cookies, forwarding headers and provider overrides never leave the hub.
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            var apiKey = route.ReadApiKey?.Invoke();
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new("Bearer", apiKey);
            if (HttpMethods.IsPost(context.Request.Method))
            {
                if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
                    !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                    context.Request.Headers.ContainsKey("Content-Encoding"))
                {
                    await ErrorAsync(context, 415, "invalid_content_type", "Send an uncompressed application/json request.");
                    return;
                }
                var bytes = await ReadBodyAsync(context.Request, timeout.Token);
                var body = JsonNode.Parse(bytes) as JsonObject ?? throw new JsonException();
                // Stateless requests remain portable when the operator switches model providers.
                if (path == "/v1/responses" &&
                    (body["previous_response_id"] is not null || body["conversation"] is not null ||
                     body["background"]?.ToJsonString() == "true"))
                {
                    await ErrorAsync(context, 400, "unsupported_stateful_request", "Send the conversation in input; background and server-side conversation state are not routed.");
                    return;
                }
                if (output.Model is { Length: > 0 } model)
                {
                    body["model"] = model;
                    bytes = JsonSerializer.SerializeToUtf8Bytes(body);
                }
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new("application/json");
            }
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                await ErrorAsync(context, 502, "upstream_redirect", "The output redirected the request. Configure its final API base URL.");
                return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            foreach (var name in new[] { "Retry-After", "x-request-id", "x-ratelimit-limit-requests", "x-ratelimit-remaining-requests", "x-ratelimit-reset-requests" })
                if (response.Headers.TryGetValues(name, out var values)) context.Response.Headers[name] = values.ToArray();
            if (response.Content.Headers.ContentEncoding.Count > 0)
                context.Response.Headers.ContentEncoding = response.Content.Headers.ContentEncoding.ToArray();
            context.Response.Headers.CacheControl = "no-store";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    await context.Response.Body.FlushAsync(timeout.Token);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { await ErrorAsync(context, 413, "request_too_large", "Router requests are limited to 16 MiB."); }
        catch (JsonException) { await ErrorAsync(context, 400, "invalid_json", "Send a valid JSON object."); }
        catch (OperationCanceledException)
        {
            if (context.RequestAborted.IsCancellationRequested || shutdown.IsCancellationRequested) context.Abort();
            else await ErrorAsync(context, 504, "upstream_timeout", "The model request exceeded the Router's ten-minute timeout.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or CryptographicException or FormatException)
        {
            await ErrorAsync(context, 502, "upstream_unavailable", "Could not reach the model output or read its credential. Check the output configuration.");
        }
        finally { _requests.Release(); }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxRequestBytes) throw new BadHttpRequestException("Request too large.", 413);
        using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            int count;
            while ((count = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (body.Length + count > MaxRequestBytes) throw new BadHttpRequestException("Request too large.", 413);
                body.Write(buffer, 0, count);
            }
            return body.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static Task ErrorAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted) { context.Abort(); return Task.CompletedTask; }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        var json = new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "router_error", ["code"] = code } };
        return context.Response.WriteAsync(json.ToJsonString(), context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
