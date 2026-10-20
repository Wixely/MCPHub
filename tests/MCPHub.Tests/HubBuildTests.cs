using MCPHub.Core.Updates;
using Xunit;

namespace MCPHub.Tests;

/// <summary>
/// Which MCPHub this is, for updating purposes. Getting it wrong is destructive — a dev build that
/// "updated" itself would overwrite a bin folder's apphost with a bundle it cannot run — so every
/// combination is pinned here from facts rather than from a published build.
/// </summary>
public class HubBuildTests
{
    [Theory]
    [InlineData("win-x64", "C:/sbin/MCPHub/MCPHub.exe")]
    [InlineData("linux-x64", "/opt/mcphub/MCPHub")]
    public void A_single_file_publish_can_update_itself(string rid, string path)
    {
        // The entry assembly has no file of its own inside a bundle: that is the tell.
        var build = HubBuild.Describe(rid, path, entryAssemblyLocation: "", entryAssemblyName: "MCPHub");

        Assert.True(build.IsSingleFilePublish);
        Assert.False(build.IsLaunchedByAHost);
        Assert.True(build.CanSelfUpdate);
        Assert.Null(build.SelfUpdateBlocker);
        Assert.Equal(rid, build.RuntimeIdentifier);
    }

    [Fact]
    public void A_bin_folder_build_is_refused_as_not_published()
    {
        var build = HubBuild.Describe("win-x64", "C:/src/MCPHub/bin/Debug/net10.0/MCPHub.exe",
            entryAssemblyLocation: "C:/src/MCPHub/bin/Debug/net10.0/MCPHub.dll", entryAssemblyName: "MCPHub");

        Assert.False(build.IsSingleFilePublish);
        Assert.False(build.CanSelfUpdate);
        Assert.Contains("development build", build.SelfUpdateBlocker);
    }

    [Fact]
    public void A_dotnet_run_launch_is_refused_as_hosted()
    {
        var build = HubBuild.Describe("win-x64", "C:/Program Files/dotnet/dotnet.exe",
            entryAssemblyLocation: "C:/src/MCPHub/bin/Debug/net10.0/MCPHub.dll", entryAssemblyName: "MCPHub");

        Assert.True(build.IsLaunchedByAHost);
        Assert.False(build.CanSelfUpdate);
        Assert.Contains("dotnet", build.SelfUpdateBlocker);
    }

    [Fact]
    public void No_executable_path_is_refused_rather_than_guessed()
    {
        var build = HubBuild.Describe("linux-x64", processPath: null, entryAssemblyLocation: "", entryAssemblyName: "MCPHub");

        Assert.False(build.CanSelfUpdate);
        Assert.Contains("executable path", build.SelfUpdateBlocker);
    }

    [Theory]
    [InlineData("win-x64", "C:/a/Hub.exe", "Hub.exe")]
    [InlineData("win-x64", null, "MCPHub.exe")]
    [InlineData("linux-x64", null, "MCPHub")]
    [InlineData("linux-arm64", "/opt/hub/MCPHub", "MCPHub")]
    public void Executable_name_follows_the_running_file_then_the_platform_default(string rid, string? path, string expected)
    {
        var build = HubBuild.Describe(rid, path, "", "MCPHub");

        Assert.Equal(expected, build.ExecutableFileName);
    }

    [Theory]
    [InlineData("win-x64", "win-x64")]
    [InlineData("linux-x64", "linux-x64")]
    [InlineData("ubuntu.24.04-x64", "linux-x64")]      // what a distro-packaged runtime reports (seen under WSL)
    [InlineData("debian.12-arm64", "linux-arm64")]
    [InlineData("fedora.40-x64", "linux-x64")]
    [InlineData("linux-musl-x64", "linux-musl-x64")]
    [InlineData("win10-x64", "win-x64")]
    [InlineData("win7-x86", "win-x86")]
    [InlineData("osx.14-arm64", "osx-arm64")]
    [InlineData("WIN-X64", "win-x64")]
    [InlineData("any", "any")]
    public void Runtime_identifiers_are_reduced_to_the_portable_form_releases_are_named_with(string reported, string expected)
    {
        Assert.Equal(expected, HubBuild.NormalizeRuntimeIdentifier(reported));
        Assert.Equal(expected, HubBuild.Describe(reported, "/opt/hub/MCPHub", "", "MCPHub").RuntimeIdentifier);
    }

    [Fact]
    public void Detecting_the_test_host_does_not_throw_and_names_a_runtime()
    {
        var build = HubBuild.Detect();

        Assert.False(string.IsNullOrWhiteSpace(build.RuntimeIdentifier));
        // The test host is not MCPHub's own apphost, so it must never be allowed to swap itself.
        Assert.False(build.CanSelfUpdate);
    }
}
