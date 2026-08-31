using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MCPHub.Proxy;

/// <summary>
/// Dynamic MCP request handlers for the aggregator: advertise the namespaced union of upstream tools
/// (plus any in-process <see cref="ILocalToolProvider"/> tools), and route a tool call to its owner by
/// splitting the exposed name on the namespace separator. Local providers are resolved before upstreams,
/// so a local key shadows an upstream of the same key rather than the other way round.
/// Every list is filtered and every call authorized per tenant via <see cref="IToolAuthorization"/>;
/// every call is reported to the <see cref="IProxyAuditSink"/>. The defaults (allow-all, no-op audit,
/// everyone is <see cref="TenantContext.Default"/>) preserve the original single-user behavior.
/// </summary>
public sealed class ProxyHandlers
{
    private readonly IUpstreamRegistry _registry;
    private readonly IToolAuthorization _authorization;
    private readonly IProxyAuditSink _auditSink;
    private readonly ITenantResolver _tenantResolver;
    private readonly IReadOnlyList<Tool> _localTools;
    private readonly IReadOnlyDictionary<string, LocalRoute> _localRoutes;

    /// <summary>Creates handlers with the single-user defaults: allow-all, no audit, default tenant.</summary>
    public ProxyHandlers(IUpstreamRegistry registry)
        : this(registry, null, null, null)
    {
    }

    /// <summary>
    /// Creates handlers with explicit policy. Any <see langword="null"/> falls back to the
    /// single-user default (allow-all / no-op audit / every caller is <see cref="TenantContext.Default"/>).
    /// <paramref name="localToolProviders"/> adds in-process tools to the catalog; see <see cref="ILocalToolProvider"/>.
    /// </summary>
    public ProxyHandlers(
        IUpstreamRegistry registry,
        IToolAuthorization? authorization,
        IProxyAuditSink? auditSink = null,
        ITenantResolver? tenantResolver = null,
        IEnumerable<ILocalToolProvider>? localToolProviders = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _authorization = authorization ?? AllowAllToolAuthorization.Instance;
        _auditSink = auditSink ?? NullProxyAuditSink.Instance;
        _tenantResolver = tenantResolver ?? DefaultTenantResolver.Instance;
        (_localTools, _localRoutes) = IndexLocalTools(localToolProviders);
    }

    private sealed record LocalRoute(ILocalToolProvider Provider, string OriginalName);

    private static (IReadOnlyList<Tool>, IReadOnlyDictionary<string, LocalRoute>) IndexLocalTools(IEnumerable<ILocalToolProvider>? providers)
    {
        var tools = new List<Tool>();
        var routes = new Dictionary<string, LocalRoute>(StringComparer.Ordinal);
        foreach (var provider in providers ?? [])
        {
            if (string.IsNullOrWhiteSpace(provider.Key))
                throw new ArgumentException("A local tool provider must have a non-empty key.", nameof(providers));

            foreach (var tool in provider.Tools)
            {
                var exposedName = provider.Key + ProxyConstants.NamespaceSeparator + tool.Name;
                if (!routes.TryAdd(exposedName, new LocalRoute(provider, tool.Name)))
                    throw new ArgumentException($"Duplicate local tool '{exposedName}'.", nameof(providers));

                tools.Add(new Tool
                {
                    Name = exposedName,
                    Title = tool.Title,
                    Description = string.IsNullOrEmpty(tool.Description) ? $"[{provider.DisplayName}]" : $"[{provider.DisplayName}] {tool.Description}",
                    InputSchema = tool.InputSchema,
                });
            }
        }

        return (tools, routes);
    }

    /// <summary>MCP <c>tools/list</c> handler: resolves the caller's tenant and lists its visible tools.</summary>
    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> context, CancellationToken cancellationToken)
        => ValueTask.FromResult(ListTools(_tenantResolver.Resolve(context.User)));

    /// <summary>MCP <c>tools/call</c> handler: resolves the caller's tenant and routes the call.</summary>
    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => await CallToolAsync(_tenantResolver.Resolve(context.User), context.Params, cancellationToken);

    /// <summary>Lists the aggregated tools visible to <paramref name="tenant"/>.</summary>
    public ListToolsResult ListTools(TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var catalog = _registry.Catalog;

        var tools = new List<Tool>(catalog.Tools.Count);
        foreach (var tool in catalog.Tools)
        {
            if (!catalog.Routes.TryGetValue(tool.Name, out var route))
                continue;
            if (_authorization.IsToolVisible(tenant, route.ServerKey, tool.Name))
                tools.Add(tool);
        }

        foreach (var tool in _localTools)
        {
            if (_authorization.IsToolVisible(tenant, _localRoutes[tool.Name].Provider.Key, tool.Name))
                tools.Add(tool);
        }

        return new ListToolsResult { Tools = tools };
    }

    /// <summary>
    /// Routes one tool call as <paramref name="tenant"/>: authorizes, forwards to the owning upstream,
    /// and audits the outcome. Denials come back as ordinary MCP error results — indistinguishable
    /// from an unknown tool, so an ungranted tenant learns nothing about what exists.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(TenantContext tenant, CallToolRequestParams? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var exposedName = request?.Name;
        if (string.IsNullOrEmpty(exposedName))
            return Error("No tool name supplied.");

        var stopwatch = Stopwatch.StartNew();
        var digest = DigestArguments(request!.Arguments);

        if (_localRoutes.TryGetValue(exposedName, out var local))
            return await CallLocalAsync(tenant, local, exposedName, request.Arguments, digest, stopwatch, cancellationToken);

        if (!_registry.Catalog.Routes.TryGetValue(exposedName, out var route))
        {
            Audit(tenant, exposedName, digest, ToolCallOutcome.Error, stopwatch.Elapsed);
            return UnknownTool(exposedName);
        }

        if (!_authorization.IsToolVisible(tenant, route.ServerKey, exposedName) ||
            !_authorization.IsCallAllowed(tenant, route.ServerKey, exposedName))
        {
            Audit(tenant, exposedName, digest, ToolCallOutcome.Denied, stopwatch.Elapsed);
            return UnknownTool(exposedName);
        }

        try
        {
            var result = await route.Client.CallToolAsync(
                new CallToolRequestParams { Name = route.OriginalName, Arguments = request.Arguments },
                cancellationToken);

            var outcome = result.IsError is true ? ToolCallOutcome.Error : ToolCallOutcome.Success;
            Audit(tenant, exposedName, digest, outcome, stopwatch.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            Audit(tenant, exposedName, digest, ToolCallOutcome.Error, stopwatch.Elapsed);
            return Error($"Upstream call to '{route.OriginalName}' failed: {ex.Message}");
        }
    }

    private async Task<CallToolResult> CallLocalAsync(
        TenantContext tenant, LocalRoute local, string exposedName, IDictionary<string, JsonElement>? arguments,
        string digest, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var key = local.Provider.Key;
        if (!_authorization.IsToolVisible(tenant, key, exposedName) ||
            !_authorization.IsCallAllowed(tenant, key, exposedName))
        {
            Audit(tenant, exposedName, digest, ToolCallOutcome.Denied, stopwatch.Elapsed);
            return UnknownTool(exposedName);
        }

        try
        {
            IReadOnlyDictionary<string, JsonElement>? readOnlyArguments = arguments switch
            {
                null => null,
                IReadOnlyDictionary<string, JsonElement> ro => ro,
                _ => new Dictionary<string, JsonElement>(arguments),
            };
            var result = await local.Provider.CallAsync(local.OriginalName, readOnlyArguments, cancellationToken);
            var outcome = result.IsError is true ? ToolCallOutcome.Error : ToolCallOutcome.Success;
            Audit(tenant, exposedName, digest, outcome, stopwatch.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            Audit(tenant, exposedName, digest, ToolCallOutcome.Error, stopwatch.Elapsed);
            return Error($"Local tool '{exposedName}' failed: {ex.Message}");
        }
    }

    private void Audit(TenantContext tenant, string tool, string digest, ToolCallOutcome outcome, TimeSpan duration)
        => _auditSink.Record(new ToolCallAuditEvent(tenant.TenantId, tool, digest, outcome, duration, DateTimeOffset.UtcNow));

    /// <summary>SHA-256 (lowercase hex) of the arguments JSON; <c>{}</c> stands in when there are none.</summary>
    internal static string DigestArguments(IDictionary<string, JsonElement>? arguments)
    {
        var json = arguments is null ? "{}" : JsonSerializer.Serialize(arguments);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static CallToolResult UnknownTool(string exposedName)
        => Error($"Unknown tool '{exposedName}'. The owning service may have stopped — re-list tools.");

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
