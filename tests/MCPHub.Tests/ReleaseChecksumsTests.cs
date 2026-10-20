using MCPHub.Core.Updates;
using Xunit;

namespace MCPHub.Tests;

public class ReleaseChecksumsTests
{
    private const string Digest = "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08";

    [Fact]
    public void Parses_the_sha256sum_format_with_its_variations()
    {
        var text = string.Join("\n",
            "# written by sha256sum",
            "",
            $"{Digest}  MCPHub-win-x64-v0.15.0.zip",
            $"{Digest.ToLowerInvariant()} *MCPHub-linux-x64-v0.15.0.zip",   // binary-mode marker
            $"{Digest}\tdist/MCPHub-osx-arm64-v0.15.0.zip",                 // taken from a folder
            "not a checksum line at all",
            "abc  too-short-digest.zip",
            "\r\n");

        var sums = ReleaseChecksums.Parse(text);

        Assert.Equal(3, sums.Count);
        Assert.Equal(Digest.ToLowerInvariant(), sums.DigestFor("MCPHub-win-x64-v0.15.0.zip"));
        Assert.Equal(Digest.ToLowerInvariant(), sums.DigestFor("MCPHub-linux-x64-v0.15.0.zip"));
        Assert.Equal(Digest.ToLowerInvariant(), sums.DigestFor("MCPHub-osx-arm64-v0.15.0.zip"));
        Assert.Null(sums.DigestFor("too-short-digest.zip"));
        Assert.Null(sums.DigestFor("MCPHub-win-arm64-v0.15.0.zip"));
    }

    [Fact]
    public void File_names_match_regardless_of_case()
    {
        var sums = ReleaseChecksums.Parse($"{Digest}  MCPHub-win-x64-v0.15.0.zip");

        Assert.NotNull(sums.DigestFor("mcphub-WIN-x64-v0.15.0.ZIP"));
    }

    [Fact]
    public void An_empty_file_lists_nothing()
    {
        Assert.Equal(0, ReleaseChecksums.Parse(string.Empty).Count);
    }
}
