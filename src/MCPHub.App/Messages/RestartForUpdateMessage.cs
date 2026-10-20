namespace MCPHub.App.Messages;

/// <summary>
/// Sent by the Updates page once the new build is in place and its replacement process is waiting: the
/// only thing left is for this process to shut down the way Exit does, stopping every managed server, so the
/// new build can take the lock and start them again.
/// </summary>
/// <param name="Version">The version that takes over, for the log line.</param>
public sealed record RestartForUpdateMessage(string Version);
