using MCPHub.Core.Infrastructure;
using Xunit;

namespace MCPHub.Tests;

/// <summary>The argument an updater hands its replacement, parsed as leniently as the restart pid.</summary>
public class InstanceUpdateArgsTests
{
    [Fact]
    public void The_replaced_version_is_read_wherever_it_appears()
    {
        Assert.Equal("0.14.0", InstanceRestart.ParseUpdatedFromVersion([InstanceRestart.UpdatedFromSwitch, "0.14.0"]));
        Assert.Equal("0.14.0", InstanceRestart.ParseUpdatedFromVersion([InstanceRestart.WaitForExitSwitch, "4321", InstanceRestart.UpdatedFromSwitch, "0.14.0"]));
        Assert.Equal("0.14.0", InstanceRestart.ParseUpdatedFromVersion([" --UPDATED-FROM ", " 0.14.0 "]));
    }

    [Fact]
    public void An_ordinary_launch_was_not_updated_from_anything()
    {
        Assert.Null(InstanceRestart.ParseUpdatedFromVersion(null));
        Assert.Null(InstanceRestart.ParseUpdatedFromVersion([]));
        Assert.Null(InstanceRestart.ParseUpdatedFromVersion([InstanceRestart.WaitForExitSwitch, "4321"]));
    }

    [Fact]
    public void A_missing_or_switch_shaped_value_is_ignored()
    {
        Assert.Null(InstanceRestart.ParseUpdatedFromVersion([InstanceRestart.UpdatedFromSwitch]));
        Assert.Null(InstanceRestart.ParseUpdatedFromVersion([InstanceRestart.UpdatedFromSwitch, ""]));
        Assert.Null(InstanceRestart.ParseUpdatedFromVersion([InstanceRestart.UpdatedFromSwitch, "--allow-multiple-instances"]));
    }
}
