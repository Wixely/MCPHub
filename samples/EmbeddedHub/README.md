# EmbeddedHub sample

Consumer smoke test for the MCPHub packages: embeds an aggregated, multi-tenant MCP endpoint
in-process using **MCPHub.Proxy** + **MCPHub.Hosting** consumed as NuGet *packages* (never project
references), then proves the tenancy contract from the outside:

- one registry + one stdio upstream (this same executable in `serve-demo` mode);
- one `ProxyHost` with bearer tokens for two tenants with different static grants;
- tenant A lists tools tenant B cannot see; an ungranted call comes back as an MCP error;
- the audit sink prints digest-only events.

## Run it

```powershell
# from the repo root — pack first, the sample restores from artifacts/packages
pwsh eng/PackLibraries.ps1
dotnet run --project samples/EmbeddedHub/EmbeddedHub.csproj
```

Exits `0` when every expectation holds (CI runs it as the consumer gate). The project is
intentionally not part of `MCPHub.slnx`. Real consumers point NuGet at the Wixely feed
(`https://nuget.pkg.github.com/Wixely/index.json`, `read:packages` PAT) instead of the local
`artifacts/packages` source in this folder's `NuGet.config`.
