using Lokad.Bash.Commands;

namespace Lokad.Bash.Tests;

public sealed class CommandAllocationBudgetTests
{
    [Theory]
    [InlineData(-1, 0, "words")]
    [InlineData(0, -1, "characters")]
    [InlineData(long.MinValue, 0, "words")]
    [InlineData(0, long.MinValue, "characters")]
    public void NegativeChargesCannotRefundTheAllowance(long words, long characters, string parameter)
    {
        var allowance = new AllocationAllowance("limit");

        Assert.Throws<ArgumentOutOfRangeException>(parameter, () => allowance.TryCharge(words, characters));

        Assert.True(allowance.TryCharge(65536, 16 * 1024 * 1024));
        Assert.False(allowance.TryCharge(1, 0));
        Assert.False(allowance.TryCharge(0, 1));
    }

    [Fact]
    public void RejectedBatchesConsumeNeitherCount()
    {
        var allowance = new AllocationAllowance("limit");

        Assert.False(allowance.TryCharge(65537, 1));
        Assert.False(allowance.TryCharge(1, 16 * 1024 * 1024 + 1));

        Assert.True(allowance.TryCharge(65536, 16 * 1024 * 1024));
        Assert.False(allowance.TryCharge(1, 0));
        Assert.False(allowance.TryCharge(0, 1));
    }
}
