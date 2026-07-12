namespace MCPHub.Core.Slopworks;

/// <summary>
/// Structured snapshot of the Slopworks-managed vLLM server, as reported by
/// <c>Slopworks.App.exe status --json</c>. Shape mirrors <c>Slopworks.App.CliServerState</c>:
/// <c>{ containerState, apiHealthy, endpoint, model, port }</c>. All fields are best-effort — a
/// stopped or uninstalled server still returns a valid object with <c>ApiHealthy=false</c> and
/// empty strings.
/// </summary>
public sealed record SlopworksStatus(
    string ContainerState,
    bool ApiHealthy,
    string Endpoint,
    string Model,
    int Port)
{
    public static SlopworksStatus Unknown => new(ContainerState: "unknown", ApiHealthy: false, Endpoint: "", Model: "", Port: 0);
}
