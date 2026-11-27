using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class NumberFormattingTests
{
    [Theory]
    [InlineData(long.MinValue, "-9223372036854775808")]
    [InlineData(0L, "0")]
    [InlineData(1023L, "1023")]
    [InlineData(1024L, "1K")]
    [InlineData(1048575L, "1023K")]
    [InlineData(1048576L, "1M")]
    [InlineData(1073741823L, "1023M")]
    [InlineData(1073741824L, "1G")]
    [InlineData(long.MaxValue, "8589934591G")]
    public void HumanSizeKeepsItsByteSpellingAtUnitBoundaries(long value, string expected)
    {
        Assert.Equal(Encoding.ASCII.GetBytes(expected), NumberFormatting.FormatHumanSize(value).ToArray());
    }

    [Fact]
    public void SuffixedHumanSizeAllocatesJustTheResult()
    {
        const int repetitions = 128;
        ReadOnlyMemory<byte> result = default;

        AllocationAssert.SustainedReuse(() => result = NumberFormatting.FormatHumanSize(1536),
            repetitions, maximumBytes: 40L * repetitions);

        Assert.True(result.Span.SequenceEqual("1K"u8));
    }
}
