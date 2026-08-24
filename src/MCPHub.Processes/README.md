# MCPHub.Processes

Supervised launching of local MCP server executables (or any long-running child process):
spec-driven start/stop, stdout/stderr capture, periodic health probing, and — on Windows — a
kill-on-close job object so children die with the host process even on a crash or force-kill.

## Quick start

```csharp
using MCPHub.Processes;

await using var host = new ProcessHost();
host.OutputReceived += line => Console.WriteLine($"[{line.Name}/{line.Stream}] {line.Text}");
host.StateChanged  += change => Console.WriteLine($"{change.Name} -> {change.State}");

await host.StartAsync(new ProcessSpec
{
    Name = "noteworthy",
    ExecutablePath = @"C:\servers\NoteworthyMCPSharp.exe",
    WorkingDirectory = @"C:\servers",
    HealthUrl = new Uri("http://127.0.0.1:5710/healthz"),
});

// later:
await host.StopAsync("noteworthy");
```

- States: `Starting → Running` once the health URL responds (or after a short grace period when no
  health URL is given); `Unhealthy` when a running process stops answering; `Stopped` after a
  requested stop; `Faulted` on an unexpected exit or failed spawn.
- `ProcessSpec` takes working directory, arguments, and extra environment variables. Everything is
  a plain options object — no settings or path conventions are read.
- `ProcessHostOptions` tunes probe interval/grace and lets you supply the probe `HttpClient`
  (e.g. a named client from `IHttpClientFactory`).

## ServerConfigReader

`ServerConfigReader.ReadPort/ReadHost` read the effective listen port/host from a server's own
`{Name}.json` config (`Server` section, found at any nesting depth) — so a supervisor follows each
server's configuration instead of hard-coding ports:

```csharp
var port = ServerConfigReader.ReadPort(@"C:\servers\Noteworthy.json") ?? 5710;
```
