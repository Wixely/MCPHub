namespace MCPHub.AppHost;

/// <summary>
/// Owns the in-process Kestrel/ASP.NET Core web application that exposes MCPHub's single aggregated
/// MCP endpoint (<c>/mcp</c>) plus a health endpoint. Built in milestone M4 using
/// <c>AddMcpServer().WithHttpTransport().With*Handler(...)</c> and <c>app.MapMcp("/mcp")</c>;
/// kept behind this abstraction so the aggregator engine stays free of ASP.NET/Avalonia coupling.
/// </summary>
public sealed class ProxyHost
{
    // TODO(M4): StartAsync(port) / StopAsync() / RestartAsync(port) building the WebApplication.
}
