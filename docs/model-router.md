# Model Router

The Router connects multiple agents to model APIs through one stable local address. It is independent of the MCP proxy: the Router carries model requests; the Proxy page aggregates MCP tools.

## Set up

1. Open **Router**, choose **Add output**, and enter a name and the provider's API base URL. Include any required path prefix, such as `http://localhost:8000/v1` or `https://api.openai.com/v1`.
2. Enter an upstream bearer key if the provider requires one. For an unauthenticated local model, leave it blank. Optionally set **Model sent to provider** so the hub chooses the model even when an agent sends a different model name.
3. Click **Add output**, select it under **Global default**, and **Apply default**.
4. Choose **Add agent**, enter an agent name, select **Use global default** or a specific output, and **Add agent and generate key**. Copy the generated key before dismissing it. Only its hash is retained; edit the agent and use **Rotate this agent's key** if the original is lost.
5. Configure that agent's OpenAI-compatible API base URL as `http://127.0.0.1:5801/v1` and its API key as the generated input key. Keep its ordinary model setting if the output has no model override.
6. Click **Start router**. Enable **Start when MCPHub launches** and **Apply listener** if wanted.

The Router stops with the desktop application. For server/container use, the [headless Router sample](../samples/HeadlessRouter/README.md) supplies a separate entry point, portable read-only configuration and secret-file support without Avalonia.

## Listener address and port

**Bind address** takes any IP address of this machine. `127.0.0.1` (the default) accepts connections from this machine only. `0.0.0.0` accepts them on every IPv4 interface, so agents elsewhere on the network can reach the Router — keep agent keys private when doing so, since the endpoint is then exposed to anything that can route to the machine. A specific interface address binds that network alone. Host names are rejected: which of several resolved addresses got bound would not be visible.

**Apply listener** saves the address, port and launch setting, and rebinds a running Router to them immediately. MCPHub is not restarted and no route, key or output is disturbed. A listening socket cannot be moved in place, so the endpoint is stopped and started: requests in flight on the old address end, and agents reconnect on the new one. If the new address or port cannot be bound — a port already taken, or an address this machine does not own — the previous settings are restored and the listener comes back up on them, and the page says what failed.

The endpoint shown on the page is always one an agent on this machine can dial: with a wildcard bind there is no literal to connect to, so loopback is displayed. Agents on other machines use this machine's own address with the same port.

## Testing an output

**Test** on an output row, and **Test all** above the list, ask the provider for its model list (`GET {base URL}models`) using that output's stored upstream key. It generates no tokens, so testing costs nothing, and it works whether or not the Router is started — a failure here is a configuration problem, not a routing one.

A pass reports the round trip and how many models the provider offers. When the output sets **Model sent to provider**, the test also checks that name is in the list and fails naming what is available instead — the single most common reason an otherwise-correct output returns errors only once an agent uses it. Failures name what to change: a rejected key, a base URL missing or duplicating its `/v1` prefix, nothing listening on the host and port, an unresolvable host, or a redirect (which the Router does not follow, so its target must be configured directly). A provider that does not list models but answers is reported as reachable with that caveat rather than as a failure.

## Agent activity

Each agent row shows when that agent last reached the Router and how many requests it has made, so an agent that has silently stopped calling — or has never connected at all — is visible without reading any logs. A key that does not resolve to an enabled agent is counted separately and surfaced above the list, which is what a stale key on some agent looks like.

Only a timestamp and a count are kept, per agent, in `router-activity.json` beside `router.json`. No prompt, completion, model name, URL or client address is recorded. The stamp is taken when a key authenticates, so it answers "did this agent reach the hub at all", independently of whether its provider then succeeded. Removing an agent discards its history. Losing the file loses only the counts.

## Routing rules

Saved agents and outputs have explicit **Edit** buttons. Editors identify the item being changed; **Add** creates a new item, **Save changes** updates the named item, and **Cancel** discards the draft. Saving closes the editor. Editing one section does not discard a draft in the other. Key rotation is a separate immediate action.

Agent rows show their saved output assignment, including whether they follow the global default. A missing destination is shown explicitly. The agent editor previews the destination and provider model that will apply after saving. Setting an output's model does not assign any agents to that output.

- An enabled input is identified by its unique `Authorization: Bearer` key.
- An explicit per-input output takes precedence over the global default. Changing the default does not alter overrides. Switch an input back to **Use global default** to have it follow global changes.
- Output edits and route changes apply on the next request. An in-flight request keeps the output, credential and model selected when it began.
- With no applicable output, authenticated requests receive `503 route_unavailable`.
- Disabled, removed or rotated input keys receive `401`. Existing in-flight requests are not revoked; stopping the Router cancels them.
- An output cannot be removed while a default or input override references it. Reassign those routes first.
- Leaving the upstream-key field blank during an edit preserves its stored credential. **Clear stored upstream key on save** explicitly removes it.

## Supported APIs

| Agent request | Forwarded to the chosen output base URL |
| --- | --- |
| `GET /v1/models` | `models` |
| `POST /v1/chat/completions` | `chat/completions` |
| `POST /v1/responses` | `responses` |
| `POST /v1/completions` | `completions` |
| `POST /v1/embeddings` | `embeddings` |

POST bodies must be uncompressed JSON objects. Payloads pass through unchanged unless a model override is set; then only the top-level `model` field is replaced. Tool calls, tool results, multimodal JSON, and provider-specific fields remain in the payload. API support still depends on the selected provider and model. An override changes the model name, not API schemas or capabilities.

JSON responses and server-sent events stream through incrementally. Upstream status codes and bodies are preserved, along with content type, request ID, retry-after and selected request-rate-limit headers. Responses are not accumulated in memory. Client disconnects cancel upstream work; there are no automatic retries or fallbacks that could duplicate paid requests.

This version does not translate Anthropic APIs, proxy WebSockets or files, or support response retrieval, query parameters, provider organization/project headers, or OAuth. Responses requests using `previous_response_id`, `conversation`, or `background: true` are rejected: send conversation history explicitly in `input` so routing does not depend on provider-side conversation state. HTTP redirects return a Router error rather than being followed.

Limits: 16 MiB per request body, 32 concurrent forwarded requests, a 15-second connection timeout and a ten-minute total request timeout. Excess concurrency returns `429`; timeout before streaming starts returns `504`. If a stream fails after its headers have been sent, the connection is aborted instead of appending an invalid JSON error to it. Oversized requests return `413`; clients that continue uploading after rejection may observe a closed connection.

Protocol references: [OpenAI Chat API](https://developers.openai.com/api/reference/resources/chat) and [Responses API](https://developers.openai.com/api/reference/resources/responses). The Router implements the subset listed above.

## Persistence and credentials

Configuration lives in `router.json` beside MCPHub's `settings.json`. Saves replace the file atomically; failed saves leave the previous in-memory configuration active. Invalid configuration disables Router configuration changes until the file is repaired and MCPHub restarted, without stopping the rest of the app.

Routes travel between machines through **Settings → Back up and move settings**, which exports the Router category — listener settings, outputs and agents, with existing agent key hashes so those agents keep working. Upstream provider keys are excluded from an unencrypted archive and are re-wrapped for the destination machine only when the archive is exported with a password and the **Tokens and keys** category.

Input keys contain 256 bits of random data and only SHA-256 hashes are stored. Output bearer keys are encrypted with current-user Windows DPAPI. On Linux, they use the same protection level as MCPHub's existing secret store: base64 encoding in an owner-only file, **not encryption**. Windows credentials cannot be transferred to another user or OS; re-enter them after migration. Keep this configuration out of source control.

Only configured output credentials are forwarded. Input keys, agent cookies and arbitrary request headers are never forwarded. The Router does not log prompts, generated text, tokens, or request URLs. Route and credential editing is available only in the desktop UI; the model endpoint exposes no configuration API.

## Verification

Automated integration tests use isolated loopback ports and mock model APIs. They cover authentication, revocation, route changes, persistence failures, credential isolation, JSON forwarding, streaming, disconnect cancellation, redirects, request limits, and listener lifecycle. Run:

```powershell
dotnet test tests/MCPHub.Tests/MCPHub.Tests.csproj -c Release --filter FullyQualifiedName~RouterTests
```

The engine is in Core behind `IRouterConfigurationSource`. `RouterStore` adapts desktop configuration; `RouterDeploymentSource` adapts portable deployment files and secrets. `RouterHost` depends on that interface and explicit listener options, not view models, desktop paths, or DPAPI. Both entry points use the same forwarding/authentication implementation. Core references the ASP.NET Core shared framework; it is not a separately published package. No Node.js or Python tooling is required.
