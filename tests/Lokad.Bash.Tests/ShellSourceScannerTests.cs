using System.Text;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ShellSourceScannerTests
{
    [Fact]
    public void ContinuedTokensRetainPhysicalSourceRanges()
    {
        const string Source = "\\\n true &\\\n& cat >\\\n> out";
        var tokenizer = new Tokenizer(ShellSource.FromText(Source));

        Assert.Equal("true", Source[tokenizer.NextToken().Source]);
        var separator = tokenizer.PeekToken();
        Assert.Equal("&\\\n&", Source[separator.Source]);
        Assert.Equal(separator.Source, tokenizer.NextToken().Source);
        Assert.Equal("cat", Source[tokenizer.NextToken().Source]);
        Assert.Equal(">\\\n>", Source[tokenizer.NextToken().Source]);
        Assert.Equal("out", Source[tokenizer.NextToken().Source]);
    }

    [Fact]
    public void ContinuedExpansionClosersRetainPhysicalOffsets()
    {
        const string Source = "$\\\n((1+\\\n2)\\\n)tail";

        Assert.True(ArithmeticExpansionScanner.TryScan(ShellSource.FromText(Source), 0, out var end, out var expression));
        Assert.Equal("1+2", expression.ToUtf8String());
        Assert.Equal("tail", Source[(end + 1)..]);
    }

    [Theory]
    [InlineData("a\\\nb", "ab")]
    [InlineData("'a\\\nb'", "'a\\\nb'")]
    [InlineData("\"a\\\nb\"", "\"ab\"")]
    [InlineData("a\\\\\nb", "a\\\\\nb")]
    [InlineData("a\\\r\nb", "a\\\r\nb")]
    [InlineData("$(printf '%s' 'a\\\nb')", "$(printf '%s' 'a\\\nb')")]
    public void LogicalTextUsesSharedQuoteAndEscapeRules(string source, string expected)
    {
        Assert.Equal(expected, ShellSourceCursor.RemoveLineContinuations(ShellSource.FromText(source)).ToUtf8String());
    }

    [Fact]
    public void TokenSourceRangesSurviveLookaheadAndPushback()
    {
        const string Source = "  cat 'a;b' && ls /";
        var tokenizer = new Tokenizer(ShellSource.FromText(Source));
        var first = tokenizer.NextToken();
        var second = tokenizer.NextToken();
        var separator = tokenizer.PeekToken();
        var physicalPosition = tokenizer.Position;
        tokenizer.PushBack(second);
        tokenizer.PushBack(first);

        Assert.Equal("cat", Source[tokenizer.NextToken().Source]);
        Assert.Equal("'a;b'", Source[tokenizer.NextToken().Source]);
        Assert.Equal("&&", Source[tokenizer.NextToken().Source]);
        Assert.Equal(separator.Source.End.Value, physicalPosition);
        Assert.Equal(physicalPosition, tokenizer.Position);
        Assert.Equal("ls", Source[tokenizer.NextToken().Source]);
    }

    [Fact]
    public void BracedParameterScanner_SkipsNestedArithmeticAndCommandExpansions()
    {
        const string Source = "${value:-$((map[$(printf ']')]+1))}tail";

        Assert.True(BracedParameterScanner.TryScan(
            ShellSource.FromText(Source),
            0,
            out var endIndex,
            out var payload,
            out var containsCommandSubstitution));

        Assert.Equal("value:-$((map[$(printf ']')]+1))", payload.ToUtf8String());
        Assert.True(containsCommandSubstitution);
        Assert.Equal("tail", Source[(endIndex + 1)..]);
    }

    [Fact]
    public void CommandSubstitutionScanner_SkipsClosingParenthesisInsideNestedParameter()
    {
        const string Source = "$(printf '%s' \"${value:-)}\")tail";

        Assert.True(CommandSubstitutionScanner.TryScan(ShellSource.FromText(Source), 0, out var endIndex, out var payload));

        Assert.Equal("printf '%s' \"${value:-)}\"", payload.ToUtf8String());
        Assert.Equal("tail", Source[(endIndex + 1)..]);
    }

    [Fact]
    public void ArraySubscriptScanner_SkipsClosingBracketInsideCommandSubstitution()
    {
        const string Source = "map[$(printf ']')] + 1";

        var openingBracketIndex = Source.IndexOf('[');
        Assert.True(ArraySubscriptScanner.TryScan(
            ShellSource.FromText(Source),
            openingBracketIndex,
            out var closingBracketIndex));

        Assert.Equal(Source.LastIndexOf(']'), closingBracketIndex);
        Assert.Equal("$(printf ']')", Source[(openingBracketIndex + 1)..closingBracketIndex]);
    }

    [Fact]
    public void ShellSourceCursor_DoubleQuotedBackslashDoesNotEscapeOrdinaryCharacter()
    {
        var cursor = new ShellSourceCursor(ShellSource.FromText("\"\\q\""), 0);

        Assert.True(cursor.TryConsumeQuoteOrEscape());
        Assert.True(cursor.IsDoubleQuoted);
        Assert.True(cursor.TryConsumeQuoteOrEscape());

        Assert.Equal((byte)'q', cursor.Current);
    }

    [Theory]
    [InlineData("${value:-$((1 + 2)}tail")]
    [InlineData("${value:-$(echo &&)}tail")]
    public void BracedParameterScanner_InvalidNestedExpansionCannotCloseOuterExpansion(string source)
    {
        var status = BracedParameterScanner.Scan(ShellSource.FromText(source), 0, out _, out _);

        Assert.NotEqual(BracedParameterScanStatus.Found, status);
    }

    [Fact]
    public void ArithmeticExpansionScanner_InvalidNestedParameterCannotCloseOuterExpansion()
    {
        const string Source = "$((1 + ${value:-2) + 3))tail";

        Assert.False(ArithmeticExpansionScanner.TryScan(ShellSource.FromText(Source), 0, out _, out _));
    }

    [Fact]
    public void ArraySubscriptScanner_InvalidNestedCommandCannotCloseOuterSubscript()
    {
        const string Source = "map[$(echo] + next]";

        Assert.False(ArraySubscriptScanner.TryScan(ShellSource.FromText(Source), Source.IndexOf('['), out _));
    }

    [Fact]
    public void CommandSubstitutionScanner_DoesNotAcceptFirstParenthesisFromMalformedBody()
    {
        const string Source = "$(echo &&)tail";

        Assert.False(CommandSubstitutionScanner.TryScan(ShellSource.FromText(Source), 0, out _, out _));
    }

    [Theory]
    [InlineData("plain", (int)ShellScanStatus.NotFound)]
    [InlineData("$(printf ok)", (int)ShellScanStatus.Complete)]
    [InlineData("$(printf ok", (int)ShellScanStatus.Incomplete)]
    [InlineData("$(printf ')'", (int)ShellScanStatus.Incomplete)]
    [InlineData("$(printf ok &&)", (int)ShellScanStatus.Invalid)]
    [InlineData("$(printf ok |)", (int)ShellScanStatus.Invalid)]
    public void CommandSubstitutionScanner_ReportsStructuredStatus(
        string source,
        int expected)
    {
        Assert.Equal((ShellScanStatus)expected, CommandSubstitutionScanner.Scan(ShellSource.FromText(source), 0, out _, out _));
    }

    [Theory]
    [InlineData("plain", (int)ShellScanStatus.NotFound)]
    [InlineData("$((1 + 2))", (int)ShellScanStatus.Complete)]
    [InlineData("$((1 + 2)", (int)ShellScanStatus.Incomplete)]
    [InlineData("$((1 + $(printf ok &&) + 2))", (int)ShellScanStatus.Invalid)]
    public void ArithmeticExpansionScanner_ReportsStructuredStatus(
        string source,
        int expected)
    {
        Assert.Equal(
            (ShellScanStatus)expected,
            ArithmeticExpansionScanner.Scan(ShellSource.FromText(source), 0, out _, out _, out _));
    }

    [Theory]
    [InlineData("plain", (int)ShellScanStatus.NotFound)]
    [InlineData("[1]", (int)ShellScanStatus.Complete)]
    [InlineData("[1", (int)ShellScanStatus.Incomplete)]
    [InlineData("[$(printf ok &&)]", (int)ShellScanStatus.Invalid)]
    public void ArraySubscriptScanner_ReportsStructuredStatus(
        string source,
        int expected)
    {
        Assert.Equal((ShellScanStatus)expected, ArraySubscriptScanner.Scan(ShellSource.FromText(source), 0, out _));
    }

    [Theory]
    [InlineData("plain", (int)BracedParameterScanStatus.NotBraced)]
    [InlineData("${value}", (int)BracedParameterScanStatus.Found)]
    [InlineData("${value", (int)BracedParameterScanStatus.Unterminated)]
    [InlineData("${value:-$(printf ok &&)}", (int)BracedParameterScanStatus.InvalidNestedSyntax)]
    public void BracedParameterScanner_ReportsStructuredStatus(
        string source,
        int expected)
    {
        Assert.Equal(
            (BracedParameterScanStatus)expected,
            BracedParameterScanner.Scan(ShellSource.FromText(source), 0, out _, out _));
    }

    [Fact]
    public void CommandSubstitutionScanner_ScansManyCasePatternParentheses()
    {
        var source = new StringBuilder("$(case target in ");
        for (var index = 0; index < 1_000; index++)
            source.Append("pattern").Append(index).Append(") : ;; ");
        source.Append("target) printf found ;; esac)tail");

        var found = CommandSubstitutionScanner.TryScan(
            ShellSource.FromText(source.ToString()),
            0,
            out var endIndex,
            out var payload);

        Assert.True(found);
        Assert.EndsWith("esac", payload.ToUtf8String(), StringComparison.Ordinal);
        Assert.Equal("tail", source.ToString()[(endIndex + 1)..]);
    }

    [Fact]
    public void CommandSubstitutionScanner_RejectsExcessiveNestedCommands()
    {
        var depth = ShellSourceCursor.MaximumNestedExpansionDepth + 2;
        var source = string.Concat(Enumerable.Repeat("$(", depth))
            + ":"
            + new string(')', depth);

        Assert.Equal(
            ShellScanStatus.Invalid,
            CommandSubstitutionScanner.Scan(ShellSource.FromText(source), 0, out _, out _));
    }

    [Fact]
    public void CommandSubstitutionScanAllocationIsBoundedBySourceLength()
    {
        _ = CommandSubstitutionScanner.Scan(ShellSource.FromText(BuildCaseSubstitution(4)), 0, out _, out _);
        var smallerSource = BuildCaseSubstitution(100);
        var largerSource = BuildCaseSubstitution(200);
        var smallerAllocation = MeasureScanAllocation(smallerSource);
        var largerAllocation = MeasureScanAllocation(largerSource);

        Assert.True(
            largerAllocation <= smallerAllocation * 3 + 256 * 1024,
            $"Doubling case-pattern input allocated {smallerAllocation:N0} then {largerAllocation:N0} bytes.");

        static string BuildCaseSubstitution(int patternCount)
        {
            var source = new StringBuilder("$(case target in ");
            for (var index = 0; index < patternCount; index++)
                source.Append("pattern").Append(index).Append(") : ;; ");

            return source.Append("target) printf found ;; esac)").ToString();
        }

        static long MeasureScanAllocation(string source)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var status = CommandSubstitutionScanner.Scan(ShellSource.FromText(source), 0, out var endIndex, out var payload);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(ShellScanStatus.Complete, status);
            Assert.Equal(source.Length - 1, endIndex);
            Assert.False(payload.IsEmpty);
            return allocated;
        }
    }

    [Fact]
    public void NestedParameterScanAllocationIsLinearAndDepthIsBounded()
    {
        var shallow = BuildNestedParameter(64);
        var deep = BuildNestedParameter(128);
        var shallowAllocation = MeasureScanAllocation(shallow);
        var deepAllocation = MeasureScanAllocation(deep);

        Assert.True(
            deepAllocation <= shallowAllocation * 3 + 64 * 1024,
            $"Doubling nesting allocated {shallowAllocation:N0} then {deepAllocation:N0} bytes.");

        var excessive = BuildNestedParameter(ShellSourceCursor.MaximumNestedExpansionDepth + 2);
        Assert.Equal(
            BracedParameterScanStatus.InvalidNestedSyntax,
            BracedParameterScanner.Scan(ShellSource.FromText(excessive), 0, out _, out _));

        static string BuildNestedParameter(int depth) =>
            string.Concat(Enumerable.Repeat("${", depth))
            + "value"
            + new string('}', depth);

        static long MeasureScanAllocation(string source)
        {
            _ = BracedParameterScanner.TryScan(ShellSource.FromText(source), 0, out _, out _);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 100; iteration++)
            {
                if (!BracedParameterScanner.TryScan(ShellSource.FromText(source), 0, out _, out _))
                    throw new InvalidOperationException("Nested parameter scan unexpectedly failed.");
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Fact]
    public void ArraySubscriptLookahead_CachesCompleteAndIncompleteNestedBrackets()
    {
        const string Source = "a[\nb[\nc[0]=value";
        var context = new ShellScanContext();
        var lookahead = new ArraySubscriptScanner.Lookahead(ShellSource.FromText(Source), context);
        Assert.Equal(ShellScanStatus.Incomplete, lookahead.Scan(1, out _));

        // The first scan reserves all three openings. Cached lookups need no more syntax units.
        for (var index = 3; index < ShellScanContext.MaximumSyntaxUnits; index++)
            Assert.True(context.TryReserveSyntax());
        Assert.Equal(ShellScanStatus.Incomplete, lookahead.Scan(4, out _));
        Assert.Equal(ShellScanStatus.Complete, lookahead.Scan(7, out var closing));
        Assert.Equal(Source.IndexOf(']'), closing);
        Assert.False(context.ParsingLimitExceeded);
    }

    [Fact]
    public void ArrayAssignmentLookahead_CacheSharesTheSyntaxLimit()
    {
        var source = "a" + new string('[', ShellScanContext.MaximumSyntaxUnits + 1);

        var error = Assert.Throws<SyntaxException>(() => Parser.Parse(source));

        Assert.Contains("Script parsing limit exceeded", error.Message);
    }

    [Fact]
    public void ArithmeticLookahead_CacheSharesTheSyntaxLimit()
    {
        var source = ShellSource.FromText("1" + new string('[', ShellScanContext.MaximumSyntaxUnits + 1));

        var error = Assert.Throws<SyntaxException>(() => Tokenizer.ReadArithmeticWord(source, ExpansionWordSyntax.Arithmetic));

        Assert.Contains("Script parsing limit exceeded", error.Message);
    }

    [Fact]
    public void ArraySubscriptScanner_RejectsManyIncompleteSubscripts()
    {
        var incompleteSubscripts = string.Concat(Enumerable.Repeat("name[", 1_000));

        Assert.False(
            ArraySubscriptScanner.TryScan(
                ShellSource.FromText(incompleteSubscripts),
                incompleteSubscripts.IndexOf('['),
                out _));
    }
}
