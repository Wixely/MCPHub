using System.Reflection;
using System.Runtime.InteropServices;
using MCPHub.Core.Infrastructure;

namespace MCPHub.Core.Updates;

/// <summary>
/// What kind of MCPHub this process is, as far as replacing it goes: which published build matches it, and
/// whether it is the sort of build that can be swapped for a newer one at all. Releases ship one single-file
/// self-contained executable per runtime identifier, so that identifier is the "build type" an update has to
/// match — a Windows hub must never be handed the Linux file, and an arm64 machine has nothing to install.
/// </summary>
/// <param name="RuntimeIdentifier">The runtime this process was published for, e.g. <c>win-x64</c>.</param>
/// <param name="ExecutablePath">The file this process is running from, captured at startup — it has to be
/// captured, because on Linux <see cref="Environment.ProcessPath"/> follows the file through a rename.</param>
/// <param name="IsSingleFilePublish">Whether this is the one-file bundle releases ship, rather than a
/// development build spread across a bin folder.</param>
/// <param name="IsLaunchedByAHost">Whether <c>dotnet</c> is running MCPHub (a <c>dotnet run</c> launch).</param>
public sealed record HubBuild(
    string RuntimeIdentifier,
    string? ExecutablePath,
    bool IsSingleFilePublish,
    bool IsLaunchedByAHost)
{
    /// <summary>The running process, detected once. Touch it early: the path must be read before any swap.</summary>
    public static HubBuild Current { get; } = Detect();

    /// <summary>Whether this build runs on Windows, where a running executable cannot be deleted, only renamed.</summary>
    public bool IsWindows => RuntimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file name a published build of this type has, e.g. <c>MCPHub.exe</c>.</summary>
    public string ExecutableFileName =>
        Path.GetFileName(ExecutablePath) is { Length: > 0 } name ? name : DefaultExecutableFileName;

    /// <summary>What the published executable is called when nothing better is known.</summary>
    public string DefaultExecutableFileName => IsWindows ? "MCPHub.exe" : "MCPHub";

    /// <summary>Whether this process can replace its own executable with a published build.</summary>
    public bool CanSelfUpdate => SelfUpdateBlocker is null;

    /// <summary>Why this build cannot update itself, in words for the Updates page; <see langword="null"/> when it can.</summary>
    public string? SelfUpdateBlocker
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExecutablePath))
                return "MCPHub could not work out its own executable path, so it cannot replace itself. Download the release and swap the file in by hand.";
            if (IsLaunchedByAHost)
                return $"This MCPHub is running under {Path.GetFileName(ExecutablePath)} rather than from its own executable " +
                       "(a dotnet run or dotnet MCPHub.dll launch), so there is no published build to replace it with.";
            if (!IsSingleFilePublish)
                return "This is a development build spread across many files, not the single-file executable releases ship, " +
                       "so it is not replaced in place. Download the release and run that instead.";
            return null;
        }
    }

    /// <summary>Reads the running process.</summary>
    public static HubBuild Detect()
    {
        var entry = Assembly.GetEntryAssembly();
        string? location = null;
        try { location = entry?.Location; }
        catch (NotSupportedException) { /* dynamic assembly: no location, which reads as not-a-file */ }

        return Describe(RuntimeInformation.RuntimeIdentifier, Environment.ProcessPath, location, entry?.GetName().Name);
    }

    /// <summary>
    /// Classifies a process from the facts about it, kept separate from <see cref="Detect"/> so every
    /// combination can be tested without publishing anything.
    /// </summary>
    /// <param name="entryAssemblyLocation">
    /// Where the entry assembly's file is. A single-file bundle reports an empty location, because the
    /// assembly lives inside the executable rather than in a file of its own — that is what tells a
    /// published build apart from a bin folder.
    /// </param>
    public static HubBuild Describe(string runtimeIdentifier, string? processPath, string? entryAssemblyLocation, string? entryAssemblyName)
    {
        var launchedByHost = InstanceRestart.IsLaunchedByAHost(processPath, entryAssemblyName);
        var singleFile = !launchedByHost
                         && !string.IsNullOrWhiteSpace(processPath)
                         && string.IsNullOrEmpty(entryAssemblyLocation);

        return new HubBuild(NormalizeRuntimeIdentifier(runtimeIdentifier), processPath, singleFile, launchedByHost);
    }

    /// <summary>
    /// Reduces a runtime identifier to the portable form releases are named with. A build published for
    /// <c>linux-x64</c> reports exactly that, but a runtime installed from a distro package reports its own
    /// identifier — <c>ubuntu.24.04-x64</c>, <c>win10-x64</c>, <c>osx.14-arm64</c> — and no release asset
    /// carries one of those. The OS family and the architecture are what matter; the version never does.
    /// </summary>
    public static string NormalizeRuntimeIdentifier(string runtimeIdentifier)
    {
        var rid = runtimeIdentifier.Trim();
        var dash = rid.LastIndexOf('-');
        if (dash <= 0 || dash == rid.Length - 1)
            return rid;

        var platform = rid[..dash];
        var architecture = rid[(dash + 1)..];

        // "linux-musl-x64" and friends split at the last dash into "linux-musl" + "x64", which is already portable.
        if (platform.StartsWith("linux", StringComparison.OrdinalIgnoreCase))
            return $"{platform.ToLowerInvariant()}-{architecture.ToLowerInvariant()}";
        if (platform.StartsWith("win", StringComparison.OrdinalIgnoreCase))
            return $"win-{architecture.ToLowerInvariant()}";
        if (platform.StartsWith("osx", StringComparison.OrdinalIgnoreCase) || platform.StartsWith("macos", StringComparison.OrdinalIgnoreCase))
            return $"osx-{architecture.ToLowerInvariant()}";
        if (platform.StartsWith("freebsd", StringComparison.OrdinalIgnoreCase))
            return $"freebsd-{architecture.ToLowerInvariant()}";

        // A distro name with a version (ubuntu.24.04, fedora.40, debian.12, alpine.3.19…) is a Linux.
        return $"linux-{architecture.ToLowerInvariant()}";
    }
}
