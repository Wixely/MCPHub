namespace MCPHub.App.Messages;

/// <summary>Sent by a service row's "Logs" action to switch to the Logs page for that service.</summary>
public sealed record ShowLogsMessage(string ServiceName);
