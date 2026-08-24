# MCPHub Package Split — Work Plan

Plan for the MCPHub repo: extract MCPHub's embeddable parts into NuGet packages on the **Wixely
GitHub Packages feed** so other Wixely apps can embed an aggregated MCP endpoint **in-process**.
First consumer: **Banter**, a chat suite whose server/agent-supervisor wants MCPHub's proxy core
running inside its own process — one aggregated, namespaced MCP endpoint with per-agent access
control — instead of requiring the MCPHub desktop app alongside. The desktop app keeps working by
consuming its own packages (dogfood).

This document is self-contained; no Banter repo access is needed. Where it says "Banter needs X",
treat X as the acceptance requirement for the public API.

## 0. Operational note — live MCPHub instance on this machine

**Testing this work will very likely break the MCPHub instance currently running on this**
**machine** (the one serving the live aggregated MCP endpoint that agents are connected to) —
integration tests bind ports, and process-host tests may sweep child processes. If it gets
killed and needs to be brought back, start this exact instance:

```
cd C:\sbin\MCPHub
.\MCPHub.exe
```

The working directory must be `C:\sbin\MCPHub` — the app resolves its config/data relative to
where it expects to run. After starting it, wait ~20 seconds for all sub-servers to initialise
before continuing to use the endpoint.

## 1. Goal & non-goals

**Goal:** three packable areas cut along seams that already exist, plus one genuinely new seam
(multi-tenant access control) that the current single-user desktop shape lacks:

| Package | Contents (today's locations) |
|---|---|
| `MCPHub.Proxy` | `IUpstreamRegistry`/`UpstreamRegistry` (upstream MCP client pool, HTTP + stdio), `AggregatedCatalog` (namespaced tool catalog snapshot + `CatalogChanged`), `UpstreamServer`, `ProxyHandlers`, `ProxyConstants` — all of `src/MCPHub.Proxy/` — **plus the new tenancy seam (§3a)**. Depends on `ModelContextProtocol`/`.Core` + logging abstractions only. |
| `MCPHub.Hosting` | `ProxyHost` (from `src/MCPHub.AppHost/`): the in-process Kestrel host exposing the aggregated `/mcp` endpoint, Start/Stop/Restart, loopback-default binding — plus **optional bearer-token authentication** feeding the tenancy seam. Depends on `ModelContextProtocol.AspNetCore`. (Renaming the project `MCPHub.AppHost` → `MCPHub.Hosting` is suggested; keep whatever reads best — coherence over ceremony.) |
| `MCPHub.Processes` | `ServiceProcessHost`, `WindowsJobObject`, `ServerConfigReader` (from `src/MCPHub.Core/Process/`): supervised launching of local MCP server executables with job-object cleanup and per-server config/port reading. |

**Important pre-existing cleanup:** `MCPHub.Proxy.csproj` references `MCPHub.Core` but no Proxy
source uses a `MCPHub.Core` type — sever that reference first; the proxy package must not drag
the desktop-app grab-bag with it.

**Non-goals — stays in the app, do not package:**

- Install/update machinery: `ServiceManager`, `IReleaseService`/GitHub release plumbing,
  `DownloadService`, `InstalledManifestStore`, `UpdateStatusCalculator`, `ConfigMergeService`
  (desktop UX around fetching server binaries; Banter deploys its own binaries).
- `SettingsStore`/`SecretStore` (DPAPI/`ProtectedData` — Windows-desktop-shaped; packages take
  options objects, never read MCPHub settings).
- `LogStore`/`LogStoreLoggerProvider` (UI log pane), `IAppPaths`/`AppPaths` (desktop layout).
- `Agent/*` and `Slopworks/*` (app-embedded DaggerAgent integrations).
- The Avalonia UI (`MCPHub.App`) and the service catalog UI flows. The curated
  `Catalog/ServiceCatalog*` data can stay app-side for now (see §6 note).

## 2. Target repo layout

```
MCPHub.sln
├── src/
│   ├── MCPHub.Proxy/       (existing — packable; loses the Core reference, gains tenancy seam)
│   ├── MCPHub.Hosting/     (renamed from MCPHub.AppHost — packable; gains bearer auth hook)
│   ├── MCPHub.Processes/   (new, packable — Process/* moved out of MCPHub.Core)
│   ├── MCPHub.Core/        (existing — shrinks; app-only logic remains)
│   └── MCPHub.App/         (existing — consumes the three above via ProjectReference)
├── samples/
│   └── EmbeddedHub/        (new — console consumer of the published packages)
└── tests/
    └── MCPHub.Tests/       (existing — grows registry/tenancy/hosting coverage)
```

Inside the repo, the app consumes the packages via `ProjectReference` (normal solution build);
external consumers use the published NuGet packages. Same code, no publish round-trip for local
development.

## 3. Public API requirements (Banter's consumption contract)

Keep existing shapes wherever they already fit. The requirements below are the minimum.

**`MCPHub.Proxy`**

- Construct and drive the registry in-process: `ConnectAsync(key, displayName, endpoint)` /
  `ConnectStdioAsync(key, displayName, command, args)` / `DisconnectAsync` / `DisconnectAllAsync`,
  the atomic `Catalog` snapshot, and `CatalogChanged` — as today.
- No static mutable state: **two independent registries (and two hosts) in one process must
  work.** Banter's interim tenancy model may run one small host per agent; nothing may prevent
  that.
- No console writes; `Microsoft.Extensions.Logging.Abstractions` only.

### 3a. The tenancy seam (new — this is the substantive work)

Today `ProxyHandlers.ListToolsAsync`/`CallToolAsync` expose every upstream tool to every caller.
Banter's model is *tenant = one agent account*, and the proxy is the enforcement point: an agent
must not even *discover* tools it isn't granted. Introduce, in `MCPHub.Proxy`:

- `TenantContext` — at minimum a `TenantId` string. A well-known
  `TenantContext.Default` represents the current single-user behavior.
- `IToolAuthorization` — decides, per tenant: which catalog entries are **visible**
  (`tools/list` is filtered, not just call-blocked) and whether a given **call is allowed**.
  Grant granularity: upstream server key and/or individual namespaced tool name; support simple
  patterns (`azdo_*`). Default implementation: allow-all (desktop unchanged).
- `StaticToolAuthorization` — grants loaded from a plain options object
  (tenant → list of patterns). This is Banter's Phase-5 interim mode; Banter Phase 6 replaces it
  with its own dynamic implementation, which is why the *interface* is the contract.
- `IProxyAuditSink` — receives one event per tool call:
  (tenant id, namespaced tool, SHA-256 digest of the arguments JSON, outcome
  success/denied/error, duration, UTC timestamp). Default: no-op. **Raw arguments must never
  reach the sink** — digest only; the sink answers "what did that agent just touch?", not
  "what did it say?".
- `ProxyHandlers` takes these via constructor and consults them on every list/call. A denied
  call returns a proper MCP error result (not an unhandled exception), and is audited as denied.

**`MCPHub.Hosting`**

- `ProxyHost` as today (bind address/port, `EndpointUrl`, Start/Stop/Restart, loopback default),
  plus **optional bearer-token authentication**: an `ITenantAuthenticator`
  (`token → TenantContext?`) supplied via options. When configured, requests without a valid
  token are rejected; the resolved `TenantContext` must reach `ProxyHandlers` for that
  request/session. When not configured, everything runs as `TenantContext.Default` — desktop
  behavior unchanged.
- **Investigate honestly:** how per-request/per-session state flows through
  `ModelContextProtocol.AspNetCore`'s transport (MCP HTTP sessions are established once, then
  multiplexed). If binding a tenant per *request* is awkward in the SDK, binding per *session*
  at establishment is acceptable; if that is also awkward, the documented fallback is
  one-`ProxyHost`-per-tenant with a fixed `TenantContext` (cheap — hosts are slim). Record which
  of the three landed in the package README; Banter builds against whichever it is.

**`MCPHub.Processes`**

- Launch/supervise a local MCP server executable: working dir, args, env; kill-on-dispose via
  job object on Windows (keep the existing behavior); expose exit/restart events. Options object
  in, no `IAppPaths`/settings coupling.
- `ServerConfigReader`'s "read the server's own config for its port" behavior stays (that was
  the v0.5.0 fix — ports come from server config, not hard-coding).

**All packages:** no references to `MCPHub.Core`/`MCPHub.App`/settings; `net10.0`; nullable
enabled; XML docs on public surface; `TreatWarningsAsErrors`.

## 4. Work items (suggested order)

1. **Sever `MCPHub.Proxy` → `MCPHub.Core`.** Delete the project reference (no source uses it);
   solution builds, tests pass. Smallest possible first commit.
2. **Tenancy seam in `MCPHub.Proxy`.** `TenantContext`, `IToolAuthorization` (+ allow-all
   default), `StaticToolAuthorization` (pattern grants), `IProxyAuditSink` (+ no-op),
   `ProxyHandlers` filtering/authorizing/auditing. Unit tests: visibility filtering, call
   denial as MCP error, audit events with digest (and never raw args), patterns, two tenants
   seeing different catalogs from one registry.
3. **`MCPHub.Hosting`.** Rename/move `AppHost`; add `ITenantAuthenticator` + bearer handling per
   the §3a investigation; integration test: start a host over a fake upstream, call with and
   without tokens, assert filtered lists and 401/denied behavior.
4. **Extract `MCPHub.Processes`.** Move `Process/*` out of Core behind an options object; the
   app maps its settings onto the options at construction.
5. **Repoint the app.** `MCPHub.App`/`MCPHub.Core` consume the packages via `ProjectReference`.
   Behavior must be identical — this refactor ships an MCPHub release with **zero user-visible
   changes** (the proof the split is clean). The desktop proxy runs as
   `TenantContext.Default` + allow-all + no-op audit.
6. **Package metadata.** `PackageId`, version, MIT license, repo URL, per-package `README.md`,
   symbols (`snupkg`), deterministic build — mirror the conventions the Bantz repo used for its
   package split (`Directory.Build.props` + per-csproj packable properties).
7. **CI/publish.** Pack on every PR (catch packability breaks); on release tag, push the three
   packages to the Wixely GitHub Packages feed (`GITHUB_TOKEN` with `packages:write`). Bantz's
   `eng/PackLibraries.ps1` + release workflow (with an expected-package-count guard) is the
   working reference.
8. **Consumer smoke sample.** `samples/EmbeddedHub`: a console app referencing the **packages**
   (not projects) that starts a registry + one stdio upstream, starts a `ProxyHost` with
   `StaticToolAuthorization` (two tenants, different grants), and demonstrates: tenant A lists
   tools tenant B cannot see; a denied call comes back as an MCP error; the audit sink prints
   digested events. This is both docs and the proof external consumption works.

## 5. Acceptance criteria

- [ ] Three packages on the Wixely feed, restorable with a `read:packages` PAT.
- [x] `MCPHub.Proxy` has no dependency on `MCPHub.Core` (or any desktop concern).
- [x] A consumer can embed registry + host in-process and serve an aggregated `/mcp` endpoint
      with under ~20 lines of setup (the sample is the measure).
- [x] Two tenants against one registry: disjoint `tools/list` results; call to an ungranted
      tool → MCP error + audit event marked denied; granted call → success + audit event with
      args digest, no raw args anywhere.
- [x] Bearer-token mode rejects unauthenticated requests; anonymous mode preserved for desktop.
- [x] Two independent `ProxyHost` instances run in one process (per-tenant-listener fallback).
- [ ] MCPHub desktop release built from the refactored solution behaves identically to the
      previous release (manual pass of the standard flows).
- [x] CI packs on PR; release workflow publishes; packages include symbols, licenses, READMEs.

## 6. Notes for the Banter side (context, no action needed in MCPHub)

Banter will: embed `MCPHub.Proxy` + `MCPHub.Hosting` inside its server/agent-supervisor process
as the single MCP endpoint its agents (DaggerAgent instances) point at, with per-agent bearer
tokens minted by the Banter server; start with `StaticToolAuthorization` profiles from config
(its Phase 5) and later swap in its own `IToolAuthorization` fed by in-protocol admin commands,
plus an `IProxyAuditSink` that surfaces "agent X is querying Azure DevOps" into chat rooms
(its Phase 6). It will also register its own built-in storage MCP server as just another
upstream via `ConnectAsync`. That is why discovery-level filtering (not just call blocking),
the audit digest rule, multiple-hosts-per-process, and options-not-settings are hard
requirements rather than nice-to-haves.

The curated `ServiceCatalog` (known servers + config templates) is deliberately **not** in
scope; if Banter later wants it for ops tooling, it extracts cleanly as a fourth data-only
package.
