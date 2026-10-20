using MCPHub.Core.Updates;
using Xunit;

namespace MCPHub.Tests;

public class HubUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Never_checked_is_due()
    {
        Assert.True(HubUpdatePolicy.IsCheckDue(null, Now));
        Assert.Null(HubUpdatePolicy.NextCheckAt(null, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(47)]
    public void A_check_younger_than_two_days_is_not_repeated(int hoursAgo)
    {
        var last = Now.AddHours(-hoursAgo);

        Assert.False(HubUpdatePolicy.IsCheckDue(last, Now));
        Assert.Equal(last.AddHours(48), HubUpdatePolicy.NextCheckAt(last, Now));
    }

    [Theory]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(24 * 30)]
    public void A_check_two_days_old_or_older_is_due(int hoursAgo)
    {
        Assert.True(HubUpdatePolicy.IsCheckDue(Now.AddHours(-hoursAgo), Now));
    }

    [Fact]
    public void A_last_check_far_in_the_future_is_due_rather_than_silencing_checks()
    {
        // A clock put back by days must not leave the hub unchecked until it catches up.
        Assert.True(HubUpdatePolicy.IsCheckDue(Now.AddDays(3), Now));
        // A clock out by an hour is ordinary drift, and the recent check stands.
        Assert.False(HubUpdatePolicy.IsCheckDue(Now.AddHours(1), Now));
    }
}
