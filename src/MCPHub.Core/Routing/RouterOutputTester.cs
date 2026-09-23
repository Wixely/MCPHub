using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCPHub.Core.Routing;

/// <summary>Outcome of probing one model output. <paramref name="Summary"/> is safe to show verbatim.</summary>
/// <param name="Succeeded">Whether the provider answered in a way that proves the output is usable.</param>
/// <param name="Summary">One line describing what happened and, on failure, what to change.</param>
/// <param name="Elapsed">Round trip of the probe request.</param>
/// <param name="ModelCount">Models the provider listed, or <see langword="null"/> when it does not list them.</param>
/// <param name="OverrideModelFound">
/// Whether the output's model override appeared in the provider's list; <see langword="null"/> when there is
/// no override or no list to check it against.
/// </param>
public sealed record RouterOutputTestResult(
    bool Succeeded,
    string Summary,
    TimeSpan Elapsed,
    int? ModelCount = null,
    bool? OverrideModelFound = null);

/// <summary>Checks that a configured model output answers, on the credential MCPHub holds for it.</summary>
public interface IRouterOutputTester
{
    /// <summary>
    /// Probes <paramref name="output"/> with <c>GET {BaseUrl}models</c> — the one read-only call every
    /// OpenAI-compatible provider offers, so verifying costs no tokens. Never throws: every failure comes
    /// back as a result with <c>Succeeded = false</c>.
    /// </summary>
    Task<RouterOutputTestResult> TestAsync(RouterOutput output, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class RouterOutputTester : IRouterOutputTester, IDisposable
{
    /// <summary>A misconfigured host can hang; the user is waiting on a button, so fail fast.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private const int MaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _client;
    private readonly Func<RouterOutput, string?> _readApiKey;

    /// <param name="readApiKey">
    /// Unwraps the output's stored credential. Defaults to <see cref="RouterStore.ReadApiKey"/>, i.e. the
    /// desktop's at-rest protection; a headless host passes its own so no DPAPI call is attempted.
    /// </param>
    /// <param name="handler">Transport to use; the default refuses redirects, matching <see cref="RouterHost"/>.</param>
    public RouterOutputTester(Func<RouterOutput, string?>? readApiKey = null, HttpMessageHandler? handler = null)
    {
        _readApiKey = readApiKey ?? RouterStore.ReadApiKey;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        { Timeout = Timeout };
    }

    /// <inheritdoc />
    public async Task<RouterOutputTestResult> TestAsync(RouterOutput output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var started = Stopwatch.GetTimestamp();
        TimeSpan Elapsed() => Stopwatch.GetElapsedTime(started);

        Uri target;
        string? apiKey;
        try
        {
            target = new Uri(new Uri(RouterConfigurationRules.NormalizeBaseUrl(output.BaseUrl)), "models");
        }
        catch (ArgumentException ex)
        {
            return new(false, ex.Message, Elapsed());
        }

        try
        {
            apiKey = _readApiKey(output);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return new(false, "The stored upstream key could not be read. It was saved by another user or on another machine — re-enter it.", Elapsed());
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
                return new(false, $"The provider redirected to {response.Headers.Location}. Set that as the API base URL — the Router does not follow redirects.", Elapsed());

            if (!response.IsSuccessStatusCode)
                return new(false, DescribeFailure(response.StatusCode, apiKey is not null), Elapsed());

            return Interpret(output, await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false), Elapsed());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, "Test cancelled.", Elapsed());
        }
        catch (OperationCanceledException)
        {
            return new(false, $"No answer within {Timeout.TotalSeconds:0} seconds. Check the host and port, and that the provider is running.", Elapsed());
        }
        catch (HttpRequestException ex)
        {
            return new(false, DescribeTransportFailure(ex, target), Elapsed());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new(false, "Could not complete the request to this output. Check the API base URL.", Elapsed());
        }
    }

    /// <summary>
    /// Turns the provider's model list into a verdict. A 200 that is not a model list still proves the URL
    /// and credential work, so it passes with a caveat rather than failing.
    /// </summary>
    private static RouterOutputTestResult Interpret(RouterOutput output, string body, TimeSpan elapsed)
    {
        List<string> models;
        try
        {
            models = JsonNode.Parse(body) is JsonObject root && root["data"] is JsonArray data
                ? [.. data.OfType<JsonObject>().Select(item => (string?)item["id"]).Where(id => !string.IsNullOrEmpty(id)).Cast<string>()]
                : [];
        }
        catch (JsonException)
        {
            return new(true, $"Reached the provider in {Format(elapsed)}, but its answer was not JSON. The URL and key work; confirm the base URL points at the API root.", elapsed);
        }

        if (models.Count == 0)
            return new(true, $"Reached the provider in {Format(elapsed)}. It listed no models, so the model name cannot be checked here.", elapsed, 0);

        if (output.Model is not { Length: > 0 } wanted)
            return new(true, $"Reached the provider in {Format(elapsed)}. {models.Count} model(s) available; agents choose which to use.", elapsed, models.Count);

        var found = models.Contains(wanted, StringComparer.Ordinal);
        return found
            ? new(true, $"Reached the provider in {Format(elapsed)}. '{wanted}' is available among {models.Count} model(s).", elapsed, models.Count, true)
            : new(false, $"Reached the provider in {Format(elapsed)}, but it does not offer '{wanted}'. Available: {string.Join(", ", models.Take(5))}{(models.Count > 5 ? ", …" : "")}.", elapsed, models.Count, false);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (limited.Length + count > MaxResponseBytes) break;
            limited.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(limited.ToArray());
    }

    private static string DescribeFailure(HttpStatusCode status, bool sentKey) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when sentKey =>
            $"The provider rejected the stored upstream key ({(int)status}). Enter a valid key on this output.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"The provider requires a key ({(int)status}). Set this output's upstream bearer key.",
        HttpStatusCode.NotFound =>
            "404 from the provider. The base URL is probably missing or duplicating a path prefix such as /v1.",
        HttpStatusCode.MethodNotAllowed =>
            "This provider does not list models. The base URL and key are reachable, but chat requests are the only way to confirm it fully.",
        HttpStatusCode.TooManyRequests =>
            "The provider is rate-limiting (429). The URL and key look right; try again shortly.",
        _ => $"The provider answered {(int)status} {status}. Check the base URL and upstream key.",
    };

    private static string DescribeTransportFailure(HttpRequestException ex, Uri target) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => $"'{target.Host}' could not be resolved. Check the host name in the base URL.",
        HttpRequestError.ConnectionError => $"Nothing is listening on {target.Host}:{target.Port}. Start the provider, or correct the base URL.",
        HttpRequestError.SecureConnectionError => "The TLS handshake failed. Check the certificate, or use http:// for a local provider.",
        _ => "Could not reach this output. Check the API base URL and that the provider is running.",
    };

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalSeconds >= 1 ? $"{elapsed.TotalSeconds:0.0}s" : $"{elapsed.TotalMilliseconds:0}ms";

    public void Dispose() => _client.Dispose();
}
