using System.Text;
using System.Text.Json;
using MCPHub.Core.Logging;
using MCPHub.Core.Models;
using Microsoft.Extensions.Logging;
using DiagProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace MCPHub.Core.Slopworks;

/// <summary>
/// Invokes Slopworks's headless CLI (<c>Slopworks.App.exe start/stop/status</c>) as one-shot
/// subprocesses. Unlike <see cref="Agent.AgentProcessHost"/>, we don't keep a long-lived child around
/// — Slopworks itself owns the vLLM container lifecycle; MCPHub just fires commands and observes
/// via <c>status --json</c>.
/// </summary>
public interface ISlopworksCli
{
    /// <summary>The Slopworks managed-service state (installed folder + version).</summary>
    ManagedService Slopworks { get; }

    /// <summary>
    /// Runs <c>Slopworks.App.exe status --json</c> and parses the reply. Returns
    /// <see cref="SlopworksStatus.Unknown"/> if the binary isn't installed, the invocation errors,
    /// or the output doesn't parse. Never throws.
    /// </summary>
    Task<SlopworksStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <c>Slopworks.App.exe start [--model M]</c>. Returns the exit code (0 = success);
    /// stdout/stderr are streamed into the log store as they arrive.
    /// </summary>
    Task<int> StartAsync(string? model = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <c>Slopworks.App.exe stop</c>. Returns the exit code (0 = success); stdout/stderr are
    /// streamed into the log store.
    /// </summary>
    Task<int> StopAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SlopworksCli : ISlopworksCli
{
    private readonly SlopworksContext _context;
    private readonly ILogStore _logStore;
    private readonly ILogger<SlopworksCli> _logger;

    public SlopworksCli(SlopworksContext context, ILogStore logStore, ILogger<SlopworksCli> logger)
    {
        _context = context;
        _logStore = logStore;
        _logger = logger;
    }

    public ManagedService Slopworks => _context.Slopworks;

    public async Task<SlopworksStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!Slopworks.IsInstalled)
            return SlopworksStatus.Unknown;

        try
        {
            var result = await RunAsync(new[] { "status", "--json" }, streamToLog: false, cancellationToken).ConfigureAwait(false);
            // `status --json` prints one JSON object then exits 0 (healthy) or 1 (unhealthy). Both
            // exit codes carry a valid payload — we just want the parsed state either way.
            var line = result.Stdout.Trim();
            if (line.Length == 0)
                return SlopworksStatus.Unknown;

            return JsonSerializer.Deserialize(line, StatusJsonContext.Default.SlopworksStatus)
                ?? SlopworksStatus.Unknown;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Slopworks status output failed to parse as JSON.");
            return SlopworksStatus.Unknown;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Slopworks status invocation failed.");
            return SlopworksStatus.Unknown;
        }
    }

    public async Task<int> StartAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        var args = string.IsNullOrWhiteSpace(model)
            ? new[] { "start" }
            : new[] { "start", "--model", model };
        var result = await RunAsync(args, streamToLog: true, cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    public async Task<int> StopAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(new[] { "stop" }, streamToLog: true, cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <summary>
    /// Spawn the CLI, wait for exit, collect stdout/stderr. When <paramref name="streamToLog"/> is
    /// set, output lines are also appended to <see cref="ILogStore"/> under
    /// <see cref="Slopworks.LogKey"/> so the Logs page surfaces them live.
    /// </summary>
    private async Task<CliResult> RunAsync(string[] args, bool streamToLog, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Slopworks.ExecutablePath,
            WorkingDirectory = Slopworks.InstallFolder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var proc = new DiagProcess { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            if (streamToLog) _logStore.Append(MCPHub.Core.Slopworks.Slopworks.LogKey, new LogLine(DateTimeOffset.Now, LogStream.Stdout, e.Data));
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            if (streamToLog) _logStore.Append(MCPHub.Core.Slopworks.Slopworks.LogKey, new LogLine(DateTimeOffset.Now, LogStream.Stderr, e.Data));
        };

        _logger.LogInformation("slopworks {Args}", string.Join(' ', args));
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try { proc.StandardInput.Close(); } catch { /* ignore */ }

        await using var kill = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* race with natural exit */ }
        });

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return new CliResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private readonly record struct CliResult(int ExitCode, string Stdout, string Stderr);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(SlopworksStatus))]
internal sealed partial class StatusJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
