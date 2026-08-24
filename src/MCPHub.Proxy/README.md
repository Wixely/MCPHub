# MCPHub.Proxy

Embeddable MCP aggregation core. Owns a pool of upstream MCP client connections (HTTP and stdio),
folds their tools into one namespaced catalog (`serverkey__toolname`), and routes tool calls back to
the owning upstream — with a multi-tenant authorization and audit seam.

Pair it with **MCPHub.Hosting** to expose the aggregate as an HTTP `/mcp` endpoint, or drive
`ProxyHandlers` from your own MCP server host.

## Quick start

```csharp
using MCPHub.Proxy;

var registry = new UpstreamRegistry(loggerFactory);
await registry.ConnectAsync("noteworthy", "Noteworthy", new Uri("http://127.0.0.1:5710/mcp"));
await registry.ConnectStdioAsync("files", "File server", "files-mcp.exe", []);

var handlers = new ProxyHandlers(registry);   // allow-all, no audit — single-user behavior
```

`registry.Catalog` is an immutable snapshot swapped atomically; subscribe to
`registry.CatalogChanged` to observe upstreams connecting/disconnecting. Faulted upstreams retry
with capped exponential backoff. Multiple registries (and hosts) per process are supported — there
is no static mutable state.

## Multi-tenant mode

Tenancy is opt-in via the second constructor:

```csharp
var authorization = new StaticToolAuthorization(new StaticToolAuthorizationOptions
{
    Grants = new Dictionary<string, IReadOnlyList<string>>
    {
        ["agent-1"] = ["noteworthy"],          // whole upstream by server key
        ["agent-2"] = ["azdo_*", "files__read_file"], // tool-name pattern + exact namespaced tool
    },
});

var handlers = new ProxyHandlers(registry, authorization, auditSink, ClaimsTenantResolver.Instance);
```

- **`IToolAuthorization`** is the enforcement point: `tools/list` is *filtered* per tenant (an
  ungranted tool is not even discoverable), and every call is authorized. A denied call returns an
  ordinary MCP error result, indistinguishable from an unknown tool. Implement the interface for
  dynamic policies; `StaticToolAuthorization` covers fixed profiles from configuration.
- **`IProxyAuditSink`** receives one event per call: tenant id, namespaced tool, a SHA-256 digest
  of the arguments JSON, outcome (success/denied/error), duration, UTC timestamp. Raw arguments
  never reach the sink — the audit trail answers "what did that agent touch?", not "what did it say?".
- **`ITenantResolver`** maps the caller's `ClaimsPrincipal` to a `TenantContext`.
  `ClaimsTenantResolver` reads the `mcphub:tenant` claim stamped by MCPHub.Hosting's bearer
  authentication; `FixedTenantResolver` pins one tenant per handler set (one-host-per-tenant
  topology); the default resolves everyone to `TenantContext.Default`.
