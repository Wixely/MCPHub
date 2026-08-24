using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MCPHub.Proxy;

/// <summary>
/// Dynamic MCP request handlers for the aggregator: advertise the namespaced union of upstream tools,
/// and route a tool call to its owning upstream by splitting the exposed name on the namespace separator.
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

    /// <summary>Creates handlers with the single-user defaults: allow-all, no audit, default tenant.</summary>
    public ProxyHandlers(IUpstreamRegistry registry)
        : this(registry, null, null, null)
    {
    }

    /// <summary>
    /// Creates handlers with explicit policy. Any <see langword="null"/> falls back to the
    /// single-user default (allow-all / no-op audit / every caller is <see cref="TenantContext.Default"/>).
    /// </summary>
    public ProxyHandlers(
        IUpstreamRegistry registry,
        IToolAuthorization? authorization,
        IProxyAuditSink? auditSink = null,
        ITenantResolver? tenantResolver = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _authorization = authorization ?? AllowAllToolAuthorization.Instance;
        _auditSink = auditSink ?? NullProxyAuditSink.Instance;
        _tenantResolver = tenantResolver ?? DefaultTenantResolver.Instance;
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
