# MCPHub.Hosting

In-process Kestrel host for [MCPHub.Proxy](https://github.com/Wixely/MCPHub): exposes one
aggregated MCP endpoint (`/mcp`) with Start/Stop/Restart, loopback-default binding, and optional
bearer-token tenant authentication. Hosts are slim; running several in one process is supported.

## Quick start

```csharp
using MCPHub.Hosting;
using MCPHub.Proxy;

var registry = new UpstreamRegistry(loggerFactory);
await registry.ConnectAsync("noteworthy", "Noteworthy", new Uri("http://127.0.0.1:5710/mcp"));

var host = new ProxyHost(new ProxyHandlers(registry), loggerFactory);
await host.StartAsync("127.0.0.1", 5800);
// host.EndpointUrl == "http://127.0.0.1:5800/mcp"
```

Pass port `0` to let the OS pick; `host.Port` reflects the bound port after start.

## Bearer-token tenancy

```csharp
var handlers = new ProxyHandlers(registry, authorization, auditSink, ClaimsTenantResolver.Instance);

var host = new ProxyHost(handlers, loggerFactory, new ProxyHostOptions
{
    TenantAuthenticator = new StaticTenantAuthenticator(new Dictionary<string, string>
    {
        ["s3cret-token-a"] = "agent-1",
        ["s3cret-token-b"] = "agent-2",
    }),
});
```

When `TenantAuthenticator` is set, every HTTP request must carry `Authorization: Bearer <token>`;
requests without a valid token get `401`. Implement `ITenantAuthenticator` to mint/validate your
own tokens (it is async — a database lookup is fine). When it is not set, the endpoint is anonymous
and every call runs as `TenantContext.Default` — the original single-user behavior.

## How the tenant reaches the handlers (investigation result)

MCP HTTP sessions are established once and then multiplexed, so it is fair to ask which of the
three candidate bindings actually works. Verified against `ModelContextProtocol.AspNetCore` 1.4.0
by integration test:

- **Per-request binding works, and is what this package uses.** Every JSON-RPC message arrives as
  its own HTTP request; the transport carries that request's `ClaimsPrincipal` into
  `RequestContext.User` inside the handlers — even for messages multiplexed over an established
  session. The bearer middleware stamps a `mcphub:tenant` claim, and `ClaimsTenantResolver` turns
  it back into the `TenantContext` per call.
- Per-session binding is therefore unnecessary (though a session's token normally never changes).
- One-host-per-tenant remains available as a *topology choice*, not a workaround: construct each
  host's `ProxyHandlers` with a `FixedTenantResolver` and skip authentication.
