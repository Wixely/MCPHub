# Headless model router

This runnable sample proves that a future Docker/server version of MCPHub can host its model router without the desktop application. It runs **only the model Router**, not the MCP proxy, catalogue manager, agent manager or desktop UI. It references `MCPHub.Core`; its runtime dependency graph contains no Avalonia or `MCPHub.App`.

## Run locally

```powershell
dotnet run --project samples/HeadlessRouter
```

The copied `router.example.json` is deliberately empty: the listener starts on loopback port 5801 and rejects all model requests with 401 until agents are configured. The VS Code **Run/Debug Model Router (headless)** profile uses port 15801 to avoid the normal desktop Router port. Nothing starts or modifies the desktop instance.

The executable supports interactive console use, Windows Service hosting through `AddWindowsService`, and systemd through `AddSystemd`. Service installation is an operator action; this sample does not install anything.

| Environment variable | Meaning |
| --- | --- |
| `MCPHUB_ROUTER_CONFIG` | Absolute path to deployment JSON; defaults to the empty example beside the executable |
| `MCPHUB_ROUTER_BIND` | IPv4/IPv6 bind address; defaults to `127.0.0.1`; use `0.0.0.0` inside Docker |
| `MCPHUB_ROUTER_PORT` | Optional port override, 1024–65535; otherwise JSON `Port` is used |

Configuration is read-only. It does not use `AppPaths`, a user's desktop profile, `settings.json`, `router.json` from the desktop, or DPAPI. Relative secret file paths resolve against the deployment JSON directory, never the process working directory. Use absolute configuration paths for services.

## Deployment configuration

Create your own `router.json` using this shape (property names are case-sensitive):

```json
{
  "SchemaVersion": 1,
  "Port": 5801,
  "DefaultOutputId": "local-model",
  "Outputs": [
    {
      "Id": "local-model",
      "Name": "Local model",
      "BaseUrl": "http://model:8000/v1",
      "Model": "your-model-id",
      "ApiKeyFile": "/run/secrets/model_key"
    }
  ],
  "Inputs": [
    {
      "Id": "coding-agent",
      "Name": "Coding agent",
      "Enabled": true,
      "KeyFile": "/run/secrets/agent_key",
      "OutputId": null
    }
  ]
}
```

Replace `BaseUrl` with a provider reachable from the container. Container `localhost` addresses the container itself, not the host or another container. Change or omit `Model` as needed.

For each input, supply exactly one of `KeyFile`, `KeyEnvironmentVariable`, or `KeyHash` (hex SHA-256 of its bearer key). Raw input keys must have 32–256 characters; use cryptographically random keys. Configure the same raw key in the agent. Setting `OutputId` overrides the global default; null follows it.

For outputs, supply at most one of `ApiKeyFile` or `ApiKeyEnvironmentVariable`. Omit both for an unauthenticated local model. When a secret reference is supplied, a missing/empty secret is an error; authentication is never silently removed. Secret files may end with a newline. Plaintext credential properties and desktop `ProtectedApiKey` values are not accepted.

The host polls the file and referenced secrets every five seconds. A successful reload swaps inputs, routes, output credentials and model settings as one snapshot. In-flight requests keep their captured route and credential. Malformed JSON, unresolved secrets, duplicate keys, invalid routes or a port change reject the entire reload, keep the last valid configuration active, and log a redacted warning. Correcting the configuration clears the warning. **Deleting a secret file does not revoke its last loaded key**: disable/remove the input in valid configuration to revoke it. Bind/port changes and changes to process environment values require a restart.

To replace credentials while keeping routes stable, update the referenced secret file and wait for reload. Initial configuration errors fail startup; unlike the desktop fallback, the host cannot run with a silently empty configuration.

## Docker

The Dockerfile uses the normal .NET 10 runtime and the image's non-root user. It does not include the desktop project. Build from the repository root:

```powershell
docker build -f samples/HeadlessRouter/Dockerfile -t mcphub-router:local .
```

Copy the example configuration above to `samples/HeadlessRouter/router.json`, create the `secrets` directory there, and put the agent/provider keys in `agent.key` and `model.key`. Keep these files out of source control; repository ignore rules cover them. Ensure the mounted configuration and secrets are readable by the image's non-root user.

```powershell
docker compose -f samples/HeadlessRouter/compose.yaml up --build
```

The Compose example mounts configuration read-only, provides Docker secret files, binds Kestrel to all interfaces **inside** the container, and publishes port 5801 on **host loopback only**. Remove the output's secret reference and corresponding Compose secret when the provider needs no authentication. Change host publication deliberately if remote agents need access; terminate TLS at an appropriate reverse proxy for remote use. Input bearer authentication remains mandatory regardless of bind address.

No writable persistent volume is needed: configuration/secrets are operator-owned inputs. This sample provides no remote management API or web administration UI. A future full MCPHub server can implement its management surface against the same configuration-source boundary without depending on Avalonia.

## Windows Service / systemd

Example Windows publish command:

```powershell
dotnet publish samples/HeadlessRouter -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/router-win
```

Register `HeadlessRouter.exe` with the Windows Service Control Manager using an operator-chosen service account, and configure its environment with `MCPHUB_ROUTER_CONFIG` pointing to readable deployment configuration. The service name is **MCPHub Model Router**. Do not rely on an interactive user's environment being available to the service.

For systemd, publish for `linux-x64` and use a unit such as:

```ini
[Unit]
Description=MCPHub Model Router
After=network-online.target

[Service]
Type=notify
User=mcphub
WorkingDirectory=/opt/mcphub-router
ExecStart=/opt/mcphub-router/HeadlessRouter
Environment=MCPHUB_ROUTER_CONFIG=/etc/mcphub-router/router.json
Environment=MCPHUB_ROUTER_BIND=127.0.0.1
Restart=on-failure
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
```

Provision the service user and readable configuration/secret files separately. The application uses the standard .NET runtime rather than NativeAOT: this sample follows MCPHub's existing ASP.NET Core hosting and service-lifetime dependencies; AOT publishing has not been verified.

## Verification boundary

Verified on Windows: solution build, automated deployment-source and HTTP integration tests, standalone console startup, authentication rejection and graceful Ctrl+C shutdown, and no desktop dependencies in `HeadlessRouter.deps.json`. Docker/Compose execution, Linux/systemd operation, and SCM installation have not been run in this environment. They remain deployment acceptance checks rather than claims of tested platform support.

Hosting references: [Microsoft Windows Service guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service), [.NET container guidance](https://learn.microsoft.com/en-us/dotnet/core/docker/introduction).
