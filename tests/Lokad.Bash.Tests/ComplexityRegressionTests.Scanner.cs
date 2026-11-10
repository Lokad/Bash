using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed partial class ComplexityRegressionTests
{
    [Fact]
    public void CommandChainParsingAllocationScalesWithSourceLength()
    {
        _ = ShellCommandParser.TryParseChain("cat a | sort; cat b");
        var smaller = Measure(100);
        var larger = Measure(200);

        Assert.True(
            larger <= smaller * 3 + 64 * 1024,
            $"Doubling chain input allocated {smaller:N0} then {larger:N0} bytes.");

        static long Measure(int count)
        {
            var source = string.Join(";", Enumerable.Repeat("cat a | sort", count));
            var before = GC.GetAllocatedBytesForCurrentThread();
            var segments = ShellCommandParser.TryParseChain(source);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.NotNull(segments);
            Assert.Equal(count, segments.Count);
            Assert.All(segments, segment => Assert.Equal("cat a | sort", segment.Text));
            return allocated;
        }
    }

    [Fact]
    public void CompoundArraySubscriptScanDoesNotAllocatePerOrdinaryCharacter()
    {
        var expectedIndex = "outer[" + new string('a', 100_000) + "]";
        var source = "[" + expectedIndex + "]=value";
        var shellSource = ShellSource.FromText(source);
        _ = ReadItem();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var item = ReadItem();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(item.Element);
        Assert.Equal(expectedIndex, item.Element.Subscript.ToUtf8String());
        Assert.Equal("value", Assert.Single(item.Element.Value.Parts).GetUtf8Text());
        Assert.True(
            allocated < source.Length * 8L,
            $"Splitting a {source.Length:N0}-character subscript allocated {allocated:N0} bytes.");

        CompoundArrayItem ReadItem()
        {
            // Warm code, not bracket matches: each measurement must scan fresh input.
            var tokenizer = new Tokenizer(shellSource);
            Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
            return tokenizer.ReadCompoundArrayItem(token);
        }
    }
}
