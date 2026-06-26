using MCPHub.Core.Models;
using MCPHub.Core.Services;
using Xunit;

namespace MCPHub.Tests;

public class UpdateStatusCalculatorTests
{
    [Fact]
    public void Null_installed_is_NotInstalled()
        => Assert.Equal(UpdateStatus.NotInstalled, UpdateStatusCalculator.Compute(null, "1.0.0"));

    [Fact]
    public void Null_latest_is_Unknown()
        => Assert.Equal(UpdateStatus.Unknown, UpdateStatusCalculator.Compute("1.0.0", null));

    [Theory]
    [InlineData("1.0.2", "1.0.2")] // equal
    [InlineData("1.1.0", "1.0.9")] // installed newer than latest
    [InlineData("2.0.0", "1.9.9")]
    public void Equal_or_newer_is_UpToDate(string installed, string latest)
        => Assert.Equal(UpdateStatus.UpToDate, UpdateStatusCalculator.Compute(installed, latest));

    [Theory]
    [InlineData("1.0.2", "1.1.6")]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.0.9", "1.1.0")]
    public void Older_is_UpdateAvailable(string installed, string latest)
        => Assert.Equal(UpdateStatus.UpdateAvailable, UpdateStatusCalculator.Compute(installed, latest));
}
