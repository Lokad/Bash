using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed partial class ComplexityRegressionTests
{
    [Fact]
    public void ContinuedMixedExpansionsDoNotReparseNestedCommandsExponentially()
    {
        _ = Parser.Parse(BuildMixedExpansion(2, continued: true));
        var smaller = Measure(4);
        var larger = Measure(8);

        Assert.True(larger <= smaller * 4 + 64 * 1024,
            $"Doubling continued expansion depth allocated {smaller:N0} then {larger:N0} bytes.");

        static long Measure(int depth)
        {
            var source = BuildMixedExpansion(depth, continued: true);
            var before = GC.GetAllocatedBytesForCurrentThread();
            var script = Parser.Parse(source);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Single(script.Commands);
            return allocated;
        }
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public async Task MixedExpansionContinuationsPreserveExecution(int depth, bool continued)
    {
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await host.RunAsync(BuildMixedExpansion(depth, continued), deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("v", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    private static string BuildMixedExpansion(int depth, bool continued)
    {
        var value = "v";
        for (var index = 0; index < depth; index++)
            value = "$(printf %s ${x:-" + (continued ? "\\\n" : "") + value + "})";
        return "printf %s \"" + value + "\"";
    }

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
