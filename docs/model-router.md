# Model Router

The Router connects multiple agents to model APIs through one stable local address. It is independent of the MCP proxy: the Router carries model requests; the Proxy page aggregates MCP tools.

## Set up

1. Open **Router**, choose **Add output**, and enter a name and the provider's API base URL. Include any required path prefix, such as `http://localhost:8000/v1` or `https://api.openai.com/v1`.
2. Enter an upstream bearer key if the provider requires one. For an unauthenticated local model, leave it blank. Optionally set **Model sent to provider** so the hub chooses the model even when an agent sends a different model name.
3. Click **Add output**, select it under **Global default**, and **Apply default**.
4. Choose **Add agent**, enter an agent name, select **Use global default** or a specific output, and **Add agent and generate key**. Copy the generated key before dismissing it. Only its hash is retained; edit the agent and use **Rotate this agent's key** if the original is lost.
5. Configure that agent's OpenAI-compatible API base URL as `http://127.0.0.1:5801/v1` and its API key as the generated input key. Keep its ordinary model setting if the output has no model override.
6. Click **Start router**. Enable **Start when MCPHub launches** and **Save listener** if wanted.

The desktop Router binds only to IPv4 loopback and stops with the desktop application. Change its port while stopped if the default conflicts with another application. For server/container use, the [headless Router sample](../samples/HeadlessRouter/README.md) supplies a separate entry point, configurable binding, portable read-only configuration and secret-file support without Avalonia.

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

Input keys contain 256 bits of random data and only SHA-256 hashes are stored. Output bearer keys are encrypted with current-user Windows DPAPI. On Linux, they use the same protection level as MCPHub's existing secret store: base64 encoding in an owner-only file, **not encryption**. Windows credentials cannot be transferred to another user or OS; re-enter them after migration. Keep this configuration out of source control.

Only configured output credentials are forwarded. Input keys, agent cookies and arbitrary request headers are never forwarded. The Router does not log prompts, generated text, tokens, or request URLs. Route and credential editing is available only in the desktop UI; the model endpoint exposes no configuration API.

## Verification

Automated integration tests use isolated loopback ports and mock model APIs. They cover authentication, revocation, route changes, persistence failures, credential isolation, JSON forwarding, streaming, disconnect cancellation, redirects, request limits, and listener lifecycle. Run:

```powershell
dotnet test tests/MCPHub.Tests/MCPHub.Tests.csproj -c Release --filter FullyQualifiedName~RouterTests
```

The engine is in Core behind `IRouterConfigurationSource`. `RouterStore` adapts desktop configuration; `RouterDeploymentSource` adapts portable deployment files and secrets. `RouterHost` depends on that interface and explicit listener options, not view models, desktop paths, or DPAPI. Both entry points use the same forwarding/authentication implementation. Core references the ASP.NET Core shared framework; it is not a separately published package. No Node.js or Python tooling is required.
