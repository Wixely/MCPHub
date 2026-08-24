// EmbeddedHub — consumer smoke sample for the MCPHub packages.
//
// Normal mode: embeds an aggregated, multi-tenant MCP endpoint in-process (registry + one stdio
// upstream + ProxyHost with bearer tokens and static grants), then plays both tenants against it
// and prints what each can see and do. Exits non-zero if any expectation fails, so CI can run it.
//
// "serve-demo" mode: the same executable acts as the stdio upstream MCP server (greet/reverse).

using System.Text.Json;
using MCPHub.Hosting;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args is ["serve-demo"])
{
    await RunDemoUpstreamAsync();
    return 0;
}

var failures = 0;

// ---- the embedded hub: this is the ~20-line setup a consumer writes -------------------------

var registry = new UpstreamRegistry(NullLoggerFactory.Instance);
await registry.ConnectStdioAsync("demo", "Demo server",
    Environment.ProcessPath!, ["serve-demo"]);

var authorization = new StaticToolAuthorization(new StaticToolAuthorizationOptions
{
    Grants = new Dictionary<string, IReadOnlyList<string>>
    {
        ["alice"] = ["greet"],    // may only greet
        ["bob"] = ["reverse"],    // may only reverse
    },
});
var audit = new ConsoleAuditSink();
var handlers = new ProxyHandlers(registry, authorization, audit, ClaimsTenantResolver.Instance);

var host = new ProxyHost(handlers, NullLoggerFactory.Instance, new ProxyHostOptions
{
    TenantAuthenticator = new StaticTenantAuthenticator(new Dictionary<string, string>
    {
        ["token-alice"] = "alice",
        ["token-bob"] = "bob",
    }),
});
await host.StartAsync("127.0.0.1", port: 0);
Console.WriteLine($"Aggregated MCP endpoint listening at {host.EndpointUrl}");

// ---- demo: two tenants against one endpoint --------------------------------------------------

try
{
    await using var alice = await ConnectAsync(host.EndpointUrl, "token-alice");
    await using var bob = await ConnectAsync(host.EndpointUrl, "token-bob");

    var aliceTools = (await alice.ListToolsAsync(new ListToolsRequestParams())).Tools.Select(t => t.Name).ToList();
    var bobTools = (await bob.ListToolsAsync(new ListToolsRequestParams())).Tools.Select(t => t.Name).ToList();
    Console.WriteLine($"\nalice sees: {string.Join(", ", aliceTools)}");
    Console.WriteLine($"bob sees:   {string.Join(", ", bobTools)}");
    Expect(aliceTools.SequenceEqual(["demo__greet"]), "alice sees exactly demo__greet");
    Expect(bobTools.SequenceEqual(["demo__reverse"]), "bob sees exactly demo__reverse");

    var greeting = await alice.CallToolAsync(new CallToolRequestParams
    {
        Name = "demo__greet",
        Arguments = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement("Banter") },
    });
    Console.WriteLine($"\nalice calls demo__greet: {Text(greeting)}");
    Expect(greeting.IsError is not true && Text(greeting) == "Hello, Banter!", "granted call succeeds");

    var denied = await alice.CallToolAsync(new CallToolRequestParams
    {
        Name = "demo__reverse",
        Arguments = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("secret") },
    });
    Console.WriteLine($"alice calls demo__reverse: IsError={denied.IsError} ({Text(denied)})");
    Expect(denied.IsError is true, "ungranted call comes back as an MCP error result");

    Expect(audit.Events.Any(e => e is { TenantId: "alice", Tool: "demo__greet", Outcome: ToolCallOutcome.Success }),
        "audit recorded alice's successful greet");
    Expect(audit.Events.Any(e => e is { TenantId: "alice", Tool: "demo__reverse", Outcome: ToolCallOutcome.Denied }),
        "audit recorded alice's denied reverse");
    Expect(audit.Events.All(e => !e.ArgumentsSha256.Contains("secret") && !e.ArgumentsSha256.Contains("Banter")),
        "audit events carry digests, never raw arguments");
}
finally
{
    await host.StopAsync();
    await registry.DisconnectAllAsync();
}

Console.WriteLine(failures == 0 ? "\nAll expectations held." : $"\n{failures} expectation(s) FAILED.");
return failures == 0 ? 0 : 1;

// ---- helpers ---------------------------------------------------------------------------------

void Expect(bool condition, string what)
{
    Console.WriteLine($"  [{(condition ? "ok" : "FAIL")}] {what}");
    if (!condition)
        failures++;
}

static string Text(CallToolResult result)
    => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

static async Task<McpClient> ConnectAsync(string endpoint, string token)
    => await McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }, NullLoggerFactory.Instance),
        clientOptions: null, NullLoggerFactory.Instance);

// The stdio upstream this sample connects to: greet + reverse over stdin/stdout.
static async Task RunDemoUpstreamAsync()
{
    await using var server = McpServer.Create(
        new StreamServerTransport(Console.OpenStandardInput(), Console.OpenStandardOutput(), "demo"),
        new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "demo", Version = "1.0" },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (context, ct) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools =
                    [
                        new Tool { Name = "greet", Description = "Greets the given name." },
                        new Tool { Name = "reverse", Description = "Reverses the given text." },
                    ],
                }),
                CallToolHandler = (context, ct) => ValueTask.FromResult(context.Params?.Name switch
                {
                    "greet" => Result($"Hello, {context.Params.Arguments?["name"].GetString()}!"),
                    "reverse" => Result(new string((context.Params.Arguments?["text"].GetString() ?? "").Reverse().ToArray())),
                    _ => new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "unknown tool" }] },
                }),
            },
        },
        loggerFactory: null,
        serviceProvider: null);

    await server.RunAsync();

    static CallToolResult Result(string text)
        => new() { Content = [new TextContentBlock { Text = text }] };
}

// Prints one line per proxied call — tenant, tool, outcome, and the arguments digest.
sealed class ConsoleAuditSink : IProxyAuditSink
{
    private readonly List<ToolCallAuditEvent> _events = [];

    public IReadOnlyList<ToolCallAuditEvent> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public void Record(ToolCallAuditEvent auditEvent)
    {
        lock (_events) _events.Add(auditEvent);
        Console.WriteLine($"  [audit] {auditEvent.TimestampUtc:HH:mm:ss.fff} tenant={auditEvent.TenantId} tool={auditEvent.Tool} outcome={auditEvent.Outcome} argsSha256={auditEvent.ArgumentsSha256[..12]}… ({auditEvent.Duration.TotalMilliseconds:F0} ms)");
    }
}
