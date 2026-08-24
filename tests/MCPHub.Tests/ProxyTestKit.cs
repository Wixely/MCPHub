using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCPHub.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MCPHub.Tests;

/// <summary>Shared plumbing for proxy tenancy/hosting tests.</summary>
internal static class ProxyTestKit
{
    /// <summary>Registry test double with a directly settable catalog.</summary>
    public sealed class FakeRegistry : IUpstreamRegistry
    {
        public AggregatedCatalog Catalog { get; set; } = AggregatedCatalog.Empty;

        public IReadOnlyCollection<UpstreamServer> Upstreams => [];

        public event Action? CatalogChanged { add { } remove { } }

        public Task ConnectAsync(string key, string displayName, Uri endpoint, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ConnectStdioAsync(string key, string displayName, string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DisconnectAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAllAsync() => Task.CompletedTask;
    }

    /// <summary>Audit sink that keeps every event for assertions.</summary>
    public sealed class CollectingAuditSink : IProxyAuditSink
    {
        private readonly List<ToolCallAuditEvent> _events = [];

        public IReadOnlyList<ToolCallAuditEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public void Record(ToolCallAuditEvent auditEvent)
        {
            lock (_events) _events.Add(auditEvent);
        }
    }

    /// <summary>Builds a catalog of routed tools; routes carry a null client (fine unless a call is forwarded).</summary>
    public static FakeRegistry RegistryWith(params (string ServerKey, string ToolName)[] tools)
        => RegistryWith(client: null, tools);

    public static FakeRegistry RegistryWith(McpClient? client, params (string ServerKey, string ToolName)[] tools)
    {
        var advertised = new List<Tool>();
        var routes = new Dictionary<string, ToolRoute>(StringComparer.Ordinal);
        foreach (var (serverKey, toolName) in tools)
        {
            var exposed = serverKey + ProxyConstants.NamespaceSeparator + toolName;
            advertised.Add(new Tool { Name = exposed });
            routes[exposed] = new ToolRoute(client!, toolName, serverKey);
        }

        return new FakeRegistry { Catalog = new AggregatedCatalog(advertised, routes) };
    }

    public static StaticToolAuthorization Grants(params (string Tenant, string[] Patterns)[] grants)
        => new(new StaticToolAuthorizationOptions
        {
            Grants = grants.ToDictionary(g => g.Tenant, g => (IReadOnlyList<string>)g.Patterns),
        });

    public static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    public static string Text(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>Recomputes the audit digest contract: SHA-256 hex of the arguments JSON (or <c>{}</c>).</summary>
    public static string ExpectedDigest(IDictionary<string, JsonElement>? arguments)
    {
        var json = arguments is null ? "{}" : JsonSerializer.Serialize(arguments);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Starts a fully in-process MCP server (stream transport over pipes) exposing an <c>echo</c>
    /// tool and an always-erroring <c>boom</c> tool, and returns a connected client for it.
    /// </summary>
    public static async Task<McpClient> StartInProcessUpstreamAsync(CancellationToken cancellationToken)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(),
                "fake-upstream", NullLoggerFactory.Instance),
            new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "fake-upstream", Version = "1.0" },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (context, ct) => ValueTask.FromResult(new ListToolsResult
                    {
                        Tools =
                        [
                            new Tool { Name = "echo", Description = "Echoes the msg argument." },
                            new Tool { Name = "boom", Description = "Always reports a tool error." },
                        ],
                    }),
                    CallToolHandler = (context, ct) => ValueTask.FromResult(context.Params?.Name switch
                    {
                        "echo" => new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = "echo:" + context.Params.Arguments?["msg"].GetString() }],
                        },
                        "boom" => new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = "boom failed" }],
                        },
                        _ => throw new InvalidOperationException("unexpected tool"),
                    }),
                },
            },
            NullLoggerFactory.Instance,
            serviceProvider: null);

        _ = server.RunAsync(cancellationToken);

        return await McpClient.CreateAsync(
            new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                NullLoggerFactory.Instance),
            clientOptions: null,
            NullLoggerFactory.Instance,
            cancellationToken);
    }
}
