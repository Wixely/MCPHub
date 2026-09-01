using Avalonia;
using MCPHub.Core.Infrastructure;

namespace MCPHub.App;

internal static class Program
{
    /// <summary>Returned when startup is refused because another MCPHub already holds the lock.</summary>
    private const int AlreadyRunningExitCode = 2;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        // Claimed before anything else starts: by the time Avalonia is up, the proxy port and the
        // auto-start services are already being taken, which is exactly what a second instance
        // must not do. AppPaths is constructed directly because DI is not composed until later.
        SingleInstanceGuard? guard = null;
        if (!SingleInstanceGuard.IsOverrideRequested(args))
        {
            var lockFilePath = SingleInstanceGuard.DefaultLockFilePath(new AppPaths());
            switch (SingleInstanceGuard.Acquire(lockFilePath, out guard))
            {
                case SingleInstanceOutcome.AlreadyHeld:
                    Console.Error.WriteLine(
                        $"MCPHub is already running on this machine, so this instance will not start. " +
                        $"It may be minimised to the notification area - look for the tray icon. " +
                        $"Pass {SingleInstanceGuard.OverrideSwitch} to start an additional instance anyway, " +
                        $"accepting that both will compete for the proxy port and the shared servers folder. " +
                        $"(Lock file: {lockFilePath})");
                    return AlreadyRunningExitCode;

                case SingleInstanceOutcome.LockUnavailable:
                    // Not a second instance — the lock simply has nowhere to live. Starting unguarded
                    // beats being unstartable on a machine whose data directory is unwritable.
                    Console.Error.WriteLine(
                        $"MCPHub could not create its single-instance lock at {lockFilePath}, so it is starting " +
                        $"without one. Check that the folder is writable; until it is, a second instance will not be refused.");
                    break;
            }
        }

        using (guard)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        return 0;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
