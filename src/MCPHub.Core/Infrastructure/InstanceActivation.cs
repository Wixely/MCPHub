using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MCPHub.Core.Infrastructure;

/// <summary>
/// Lets a launch that <see cref="SingleInstanceGuard"/> refuses hand the user over to the MCPHub already
/// running, instead of failing silently into the void. Double-clicking the shortcut a second time then does
/// what the user meant by it — the existing window comes forward, exactly as clicking the tray icon does.
/// <para>
/// The transport is a named pipe scoped to the current user (<see cref="PipeOptions.CurrentUserOnly"/>), so
/// another account on the same machine can neither summon nor impersonate this one. The protocol carries no
/// instruction beyond "show yourself": there is nothing here for a caller to ask for that is worth guarding.
/// </para>
/// </summary>
public static class InstanceActivation
{
    /// <summary>Greeting the owner sends on connect, followed by its process id.</summary>
    private const string Greeting = "MCPHUB1";

    /// <summary>The only request the owner accepts.</summary>
    private const string ShowRequest = "SHOW";

    /// <summary>Sent once the owner has actually raised its window.</summary>
    private const string Acknowledgement = "OK";

    /// <summary>Long enough for a busy UI thread to answer, short enough not to hang a double-click.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Channel name for the instance whose lock lives at <paramref name="lockFilePath"/>. Derived from that
    /// path so instances with separate data directories never share a channel, and hashed because a pipe name
    /// may not contain a path separator.
    /// </summary>
    public static string ChannelNameFor(string lockFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        var normalized = Path.GetFullPath(lockFilePath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "mcphub-activate-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// Asks the running instance to show itself. Returns <see langword="false"/> when nothing answers within
    /// <paramref name="timeout"/> — an MCPHub too wedged to respond, or one from a build without this channel
    /// — so the caller can fall back to explaining itself rather than exiting as though it had succeeded.
    /// </summary>
    public static bool TryRequestActivation(string channelName, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        var deadline = timeout ?? DefaultTimeout;

        try
        {
            using var client = new NamedPipeClientStream(".", channelName, PipeDirection.InOut,
                PipeOptions.CurrentUserOnly);
            client.Connect((int)deadline.TotalMilliseconds);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            // The owner is not the foreground process, so Windows would refuse its SetForegroundWindow and
            // merely flash its taskbar button. This process was just launched by the user and therefore may
            // hand that right over — which is what turns "it blinked" into "the window came up".
            if (ParseOwnerProcessId(reader.ReadLine()) is { } ownerPid && OperatingSystem.IsWindows())
                AllowForeground(ownerPid);

            writer.WriteLine(ShowRequest);
            return string.Equals(reader.ReadLine(), Acknowledgement, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static int? ParseOwnerProcessId(string? greeting)
    {
        var parts = (greeting ?? string.Empty).Split(' ', 2);
        return parts.Length == 2 && parts[0] == Greeting &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static void AllowForeground(int processId)
    {
        try { AllowSetForegroundWindow((uint)processId); }
        catch (DllNotFoundException) { /* Best effort: the window still comes back, just without focus. */ }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    /// <summary>
    /// The owning instance's side of the channel: accepts activation requests for as long as it is alive and
    /// invokes the callback for each. Created once, immediately after the single-instance lock is taken.
    /// </summary>
    public sealed class Listener : IDisposable
    {
        private readonly string _channelName;
        private readonly Action _onActivationRequested;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;

        private Listener(string channelName, Action onActivationRequested, NamedPipeServerStream first)
        {
            _channelName = channelName;
            _onActivationRequested = onActivationRequested;
            _loop = Task.Run(() => AcceptLoopAsync(first, _stopping.Token));
        }

        /// <summary>
        /// Starts listening, or returns <see langword="null"/> when the channel cannot be opened. A missing
        /// activation channel costs a second launch its hand-off; it must never cost MCPHub its startup.
        /// </summary>
        public static Listener? TryStart(string channelName, Action onActivationRequested)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
            ArgumentNullException.ThrowIfNull(onActivationRequested);
            try
            {
                // The first pipe is created here rather than on the loop's thread, so a platform that cannot
                // provide one — or a name already taken — is reported to the caller instead of disappearing
                // into a background retry that never succeeds.
                return new Listener(channelName, onActivationRequested, CreateServer(channelName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                return null;
            }
        }

        // One connection at a time, re-created after each: a second launch is a rare, brief event, and the
        // client's connect call waits out the gap between accepts.
        private static NamedPipeServerStream CreateServer(string channelName) => new(
            channelName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task AcceptLoopAsync(NamedPipeServerStream first, CancellationToken cancellationToken)
        {
            var next = first;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var server = next;
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await ServeAsync(server, cancellationToken).ConfigureAwait(false);
                    next = CreateServer(_channelName);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // A dropped client, or a transient failure to re-open: try again rather than giving up the
                    // channel for the life of the process.
                    try { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    try { next = CreateServer(_channelName); }
                    catch (Exception retry) when (retry is IOException or UnauthorizedAccessException)
                    {
                        return; // The channel is not coming back; a second launch falls back to its message.
                    }
                }
            }
        }

        private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync($"{Greeting} {Environment.ProcessId}").ConfigureAwait(false);

            var request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(request, ShowRequest, StringComparison.Ordinal))
                return;

            _onActivationRequested();
            await writer.WriteLineAsync(Acknowledgement).ConfigureAwait(false);

            // Windows can discard a pipe's buffered bytes when the server closes first, which would lose the
            // acknowledgement the client uses to decide it succeeded. Unix domain sockets close gracefully.
            if (OperatingSystem.IsWindows() && server.IsConnected)
                server.WaitForPipeDrain();
        }

        public void Dispose()
        {
            _stopping.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { /* The loop only ever faults on transport errors. */ }
            _stopping.Dispose();
        }
    }
}
