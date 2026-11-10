using System.Globalization;
using System.Numerics;
using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ExtendedFloatingPointTests
{
    [Fact]
    public void ParseGeneral_BoundsConversionAllocationsForLongMantissas()
    {
        var input = Encoding.ASCII.GetBytes(new string('1', 6_000_000) + "e-5999999");
        var expected = ExtendedFloatingPoint.FromRational(10, 9, negative: false, out _);
        long workspace = 0;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var actual = ExtendedFloatingPoint.ParseGeneral(input, bytes => workspace = bytes, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, actual);
        Assert.InRange(workspace, 1, 1024 * 1024);
        Assert.InRange(allocated, 0, 4 * 1024 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseGeneral_PreservesRoundingWhenDiscardingLateDigits(bool hexadecimal)
    {
        var midpoint = hexadecimal
            ? "0x1.0000000000000001"
            : "1.0000000000000000000542101086242752217003726400434970855712890625";
        var below = hexadecimal
            ? "0x1.0000000000000000"
            : "1.0000000000000000000542101086242752217003726400434970855712890624";
        var exponent = hexadecimal ? "p0" : string.Empty;
        var lower = Parse("1");
        var upper = Parse("0x1.0000000000000002p0");

        Assert.Equal(lower, Parse(midpoint + exponent));
        Assert.Equal(lower, Parse(below + new string(hexadecimal ? 'f' : '9', 20_000) + exponent));
        Assert.Equal(upper, Parse(midpoint + new string('0', 20_000) + "1" + exponent));
    }

    [Fact]
    public void ParseGeneral_PreservesSubnormalRoundingWhenDiscardingLateDigits()
    {
        var coefficient = BigInteger.Pow(5, 16_446);
        var midpoint = coefficient.ToString(CultureInfo.InvariantCulture) + "e-16446";
        var below = (coefficient - 1).ToString(CultureInfo.InvariantCulture) + new string('9', 20_000) + "e-36446";
        var above = coefficient.ToString(CultureInfo.InvariantCulture) + new string('0', 20_000) + "1e-36447";
        var zero = Parse("0");

        Assert.Equal(zero, Parse(midpoint));
        Assert.Equal(zero, Parse(below));
        Assert.Equal(Parse("0x1p-16445"), Parse(above));
    }

    [Theory]
    [InlineData("1.25")]
    [InlineData("0x1.4p0")]
    public void ParseGeneral_ObservesCancellationBeforeConversion(string input)
    {
        using var cancellation = new CancellationTokenSource();
        var bytes = Encoding.ASCII.GetBytes(input);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ExtendedFloatingPoint.ParseGeneral(bytes, _ => cancellation.Cancel(), cancellation.Token));
    }

    private static ExtendedFloatingPoint Parse(string input) =>
        ExtendedFloatingPoint.ParseGeneral(Encoding.ASCII.GetBytes(input), _ => { }, CancellationToken.None);
}
