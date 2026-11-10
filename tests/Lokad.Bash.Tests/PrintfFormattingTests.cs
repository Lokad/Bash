using System.Text;
using Lokad.Bash.Formatting;

namespace Lokad.Bash.Tests;

public sealed class PrintfFormattingTests
{
    [Fact]
    public async Task EvaluateAsync_InterleavesSuspendingCountWritesAndDiagnostics()
    {
        var events = new List<string>();
        var result = await PrintfFormatting.EvaluateAsync(
            ShellValue.FromText("ab%n%d%n"), [ShellValue.FromText("first"), ShellValue.FromText("bad"), ShellValue.FromText("second")],
            DateTimeOffset.UnixEpoch, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes), AssignAsync, DiagnoseAsync);

        Assert.Equal(["first=2", "bad: invalid number", "second=3"], events);
        Assert.Equal("ab0", Text(result.Output));
        Assert.Equal(1, result.ExitCode);

        async ValueTask<bool> AssignAsync(PrintfVariableAssignment assignment)
        {
            await Task.Yield();
            events.Add(assignment.Name.ToValue().ToUtf8String() + "=" + assignment.Count);
            return true;
        }

        ValueTask<bool> DiagnoseAsync(ShellValue diagnostic)
        {
            events.Add(diagnostic.ToUtf8String());
            return ValueTask.FromResult(true);
        }
    }

    [Fact]
    public async Task EvaluateAsync_StopsAtRejectedCountWithoutProcessingLaterConversions()
    {
        var result = await PrintfFormatting.EvaluateAsync(ShellValue.FromText("before%n%Zafter"),
            [ShellValue.FromText("count")], DateTimeOffset.UnixEpoch, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes),
            static _ => ValueTask.FromResult(false), static _ => throw new InvalidOperationException("Unreached diagnostic."));

        Assert.Equal("before", Text(result.Output));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsWarningsBeforeTheStopEscape()
    {
        var diagnostics = new List<ShellValue>();
        var result = await PrintfFormatting.EvaluateAsync(ShellValue.FromText("%b%n"),
            [ShellValue.FromText("\\x\\c"), ShellValue.FromText("count")], DateTimeOffset.UnixEpoch, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes),
            static _ => throw new InvalidOperationException("Unreached count write."), DiagnoseAsync);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("missing hex digit for \\x", Assert.Single(diagnostics).ToUtf8String());

        ValueTask<bool> DiagnoseAsync(ShellValue diagnostic)
        {
            diagnostics.Add(diagnostic);
            return ValueTask.FromResult(true);
        }
    }

    [Theory]
    [InlineData("<%s>", new[] { "a", "b", "c" }, "<a><b><c>")]
    [InlineData("%s\\n", new[] { "a", "b" }, "a\nb\n")]
    [InlineData("<%s><%s>", new[] { "a" }, "<a><>")]
    [InlineData("<%d><%03d>", new string[0], "<0><000>")]
    [InlineData("literal", new[] { "ignored" }, "literal")]
    public void Format_ReusesFormatAndSuppliesMissingValues(
        string format,
        string[] arguments,
        string expected)
    {
        Assert.Equal(expected, Text(FormatText(format, arguments)));
    }

    [Fact]
    public void Format_SupportsIntegerFlagsWidthsAndBases()
    {
        var output = FormatText(
            "%+06d|% i|%#o|%#x|%#X|%u",
            ["-7", "7", "9", "26", "26", "-1"]);

        Assert.Equal("-00007| 7|011|0x1a|0X1A|18446744073709551615", Text(output));
    }

    [Fact]
    public void Format_AppliesNumberSpecificZeroPaddingRules()
    {
        var output = FormatText(
            "[%05.3d][%05.3u][%08.2f]",
            ["7", "7", "1.5"]);

        Assert.Equal("[  007][  007][00001.50]", Text(output));
    }

    [Fact]
    public void Format_ConsumesDynamicWidthAndPrecision()
    {
        var output = FormatText("%*.*s", ["6", "3", "abcdef"]);

        Assert.Equal("   abc", Text(output));
    }

    [Fact]
    public void Format_SupportsFloatingPointConversions()
    {
        var output = FormatText(
            "%.2f|%.1e|%.4g|%a",
            ["1.25", "12.5", "12345", "8"]);

        Assert.Equal("1.25|1.2e+01|1.234e+04|0x8p+0", Text(output));
    }

    [Fact]
    public void Format_SupportsUppercaseAndAlternateFloatingPointConversions()
    {
        var output = FormatText(
            "%E|%F|%G|%.3a|%#A",
            ["1.25", "1.25", "12", "1.1", "8"]);

        Assert.Equal("1.250000E+00|1.250000|12|0x8.ccdp-3|0X8.P+0", Text(output));
    }

    [Fact]
    public void Format_ZeroPadsHexadecimalFloatingPointAfterPrefix()
    {
        var output = FormatText("[%020a][%+020A]", ["1", "1"]);

        Assert.Equal("[0x000000000000008p-3][+0X00000000000008P-3]", Text(output));
    }

    [Fact]
    public void Format_GeneralFloatingPointChoosesNotationAfterRounding()
    {
        var output = FormatText("%.1g|%g|%#.1g", ["9.9", "999999.9", "9.9"]);

        Assert.Equal("1e+01|1e+06|1.e+01", Text(output));
    }

    [Fact]
    public void Format_SupportsCharacterStringAndIgnoredNumericLengthModifiers()
    {
        var output = FormatText(
            "%c|%s|%hhd|%lld|%jd|%zd|%td",
            ["hello", "text", "1", "2", "3", "4", "5"]);

        Assert.Equal("h|text|1|2|3|4|5", Text(output));
    }

    [Fact]
    public void Format_AppliesNegativeDynamicWidthAndPrecision()
    {
        var output = FormatText("[%*s][%.*s]", ["-5", "x", "-2", "abc"]);

        Assert.Equal("[x    ][abc]", Text(output));
    }

    [Fact]
    public void Format_ParsesBashNumericConstants()
    {
        var output = FormatText("%d|%d|%d|%u", ["010", "0x10", "'A", "-1"]);

        Assert.Equal("8|16|65|18446744073709551615", Text(output));
    }

    [Fact]
    public void Format_HandlesInfinityAndNan()
    {
        var output = FormatText("%f|%F|%g", ["inf", "nan", "-inf"]);

        Assert.Equal("inf|NAN|-inf", Text(output));
    }

    [Fact]
    public void Format_SpacePadsInfinityAndNanWhenZeroPaddingIsRequested()
    {
        var output = FormatText("[%010f][%+010f][%010f]", ["inf", "inf", "nan"]);

        Assert.Equal("[       inf][      +inf][       nan]", Text(output));
    }

    [Fact]
    public void Evaluate_AcceptsNanPayloads()
    {
        var result = EvaluateText("%f|%F", ["nan(foo_bar9)", "-nan()"], DateTimeOffset.UnixEpoch);

        Assert.Equal("nan|-NAN", Text(result.Output));
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Format_PreservesBashExtendedFloatingPointPrecision()
    {
        var output = FormatText("%.20f|%a", ["0.1", "1.1"]);

        Assert.Equal("0.10000000000000000000|0x8.ccccccccccccccdp-3", Text(output));
    }

    [Theory]
    [InlineData("1e9999", "inf")]
    [InlineData("1e-9999", "0.000000")]
    public void Evaluate_ReportsFloatingPointRangeAsSuccessfulWarning(string argument, string expected)
    {
        var result = EvaluateText("%f", [argument], DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, Text(result.Output));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"warning: {argument}: Numerical result out of range"], result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Fact]
    public void Evaluate_ReportsMalformedHexadecimalExponent()
    {
        var result = EvaluateText("%f", ["0x1p+"], DateTimeOffset.UnixEpoch);

        Assert.Equal("1.000000", Text(result.Output));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["0x1p+: invalid hex number"], result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Fact]
    public void Format_DecodesFormatAndPercentBEscapes()
    {
        var output = FormatText("\\x41\\u03a9|%b", ["a\\nb\\041\\x21\\u03a9"]);

        Assert.Equal("AΩ|a\nb!!Ω", Text(output));
    }

    [Fact]
    public void Format_EncodesBashExtendedUnicodeEscapes()
    {
        var output = FormatText(
            "\\uD800|%b|\\U00200000|\\U7FFFFFFF|x\\UFFFFFFFFy",
            ["\\U00110000"]);

        Assert.Equal(
            new byte[]
            {
                0xed, 0xa0, 0x80,
                (byte)'|',
                0xf4, 0x90, 0x80, 0x80,
                (byte)'|',
                0xf8, 0x88, 0x80, 0x80, 0x80,
                (byte)'|',
                0xfd, 0xbf, 0xbf, 0xbf, 0xbf, 0xbf,
                (byte)'|', (byte)'x', (byte)'y'
            },
            output.ToArray());
    }

    [Fact]
    public void Format_PercentBStopsAtBackslashC()
    {
        var output = FormatText("x%by", ["a\\cb", "ignored"]);

        Assert.Equal("xa", Text(output));
    }

    [Fact]
    public void Format_ByteStringPrecisionCanSplitUtf8()
    {
        var output = FormatText("%.1s", ["é"]);

        Assert.Equal(new byte[] { 0xc3 }, output.ToArray());
    }

    [Fact]
    public void Format_LengthModifiersKeepCharacterAndStringConversionsByteOriented()
    {
        var output = FormatText(
            "[%.1ls][%3ls][%3lc]",
            ["éclair", "é", "é"]);

        byte[] expected =
        [
            (byte)'[', 0xc3, (byte)']',
            (byte)'[', (byte)' ', 0xc3, 0xa9, (byte)']',
            (byte)'[', (byte)' ', (byte)' ', 0xc3, (byte)']'
        ];
        Assert.Equal(expected, output.ToArray());
    }

    [Theory]
    [InlineData('C')]
    [InlineData('S')]
    public void Evaluate_RejectsUppercaseCharacterAndStringConversions(char conversion)
    {
        var result = EvaluateText($"before%{conversion}after", ["value"], DateTimeOffset.UnixEpoch);

        Assert.Equal("before", Text(result.Output));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal([$"`{conversion}': invalid format character"], result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Theory]
    [InlineData("%q", "a b", "a\\ b")]
    [InlineData("%q", "a\nb", "$'a\\nb'")]
    [InlineData("%q", "a\u0001b", "$'a\\001b'")]
    // Bash 5.3 alternate quoting still uses ANSI-C syntax for nonprinting bytes.
    [InlineData("%#q", "a'b", "'a'\\''b'")]
    [InlineData("%#.2Q", "a'b", "'a'\\'''")]
    [InlineData("%#.1Q", "é", "$'\\303'")]
    [InlineData("%#.4q", "a b", "'a b")]
    [InlineData("%#8q", "a b", "   'a b'")]
    [InlineData("%#.1q", "", "'")]
    [InlineData("%#.0Q", "a b", "''")]
    [InlineData("%#Q", "é🙂", "'é🙂'")]
    [InlineData("%#q", "a\u0001b", "$'a\\001b'")]
    [InlineData("%#q", "a\nb", "$'a\\nb'")]
    [InlineData("%.2Q", "a b", "a\\ ")]
    [InlineData("%.1Q", "é", "$'\\303'")]
    public void Format_QuotesReusableShellInput(string format, string argument, string expected)
    {
        Assert.Equal(expected, Text(FormatText(format, [argument])));
    }

    [Fact]
    public void Evaluate_FormatsDeterministicTimestamp()
    {
        var timestamp = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var result = EvaluateText("%(%Y-%m-%d %H:%M:%S)T", ["-1"], timestamp);

        Assert.Equal("2024-01-02 03:04:05", Text(result.Output));
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Evaluate_FallsBackToEpochForTimestampOutsideDateTimeOffsetRange()
    {
        var result = EvaluateText(
            "%(%Y-%m-%d %H:%M:%S)T",
            ["253402300800"],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("1970-01-01 00:00:00", Text(result.Output));
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Format_DoesNotMaterializeTruncatedUtf8Arguments()
    {
        var argument = ShellValue.FromText(new string('é', 100_000));
        var format = ShellValue.FromText("%.1s|%.1Q");
        _ = PrintfFormatting.Format(format, [argument, argument], new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.TextToolBytes));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var output = PrintfFormatting.Format(format, [argument, argument], new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.TextToolBytes));
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        byte[] expected =
        [
            0xc3,
            (byte)'|',
            (byte)'$',
            (byte)'\'',
            (byte)'\\',
            (byte)'3',
            (byte)'0',
            (byte)'3',
            (byte)'\''
        ];
        Assert.Equal(expected, output.ToArray());
        Assert.True(allocatedBytes < 64 * 1024, $"Formatting allocated {allocatedBytes} bytes.");
    }

    [Fact]
    public void Evaluate_ReportsInvalidNumbersAndContinues()
    {
        var result = EvaluateText("%d:%d", ["12x", "3"], DateTimeOffset.UnixEpoch);

        Assert.Equal("12:3", Text(result.Output));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("12x: invalid number", result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Fact]
    public void Evaluate_ReportsNonAsciiDigitsAsInvalidNumbers()
    {
        var result = EvaluateText("%f", ["١"], DateTimeOffset.UnixEpoch);

        Assert.Equal("0.000000", Text(result.Output));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["١: invalid number"], result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Fact]
    public void Evaluate_TreatsEmptyAndMissingNumericArgumentsAsZero()
    {
        var explicitEmpty = EvaluateText("%d|%f|%u", ["", "", ""], DateTimeOffset.UnixEpoch);
        var missing = EvaluateText("%d|%f|%u", [], DateTimeOffset.UnixEpoch);

        Assert.Equal("0|0.000000|0", Text(explicitEmpty.Output));
        Assert.Equal(0, explicitEmpty.ExitCode);
        Assert.Empty(explicitEmpty.Diagnostics);
        Assert.Equal("0|0.000000|0", Text(missing.Output));
        Assert.Equal(0, missing.ExitCode);
        Assert.Empty(missing.Diagnostics);
    }

    [Fact]
    public void Evaluate_TreatsEmptyQuotedCharacterConstantsAsZero()
    {
        var result = EvaluateText("%d|%u|%f", ["'", "\"", "'"], DateTimeOffset.UnixEpoch);

        Assert.Equal("0|0|0.000000", Text(result.Output));
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Evaluate_InvalidNegativeFloatingPointUsesPositiveZero()
    {
        var result = EvaluateText("%f", ["-x"], DateTimeOffset.UnixEpoch);

        Assert.Equal("0.000000", Text(result.Output));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["-x: invalid number"], result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Fact]
    public void Evaluate_ReportsIntegerOverflowAsSuccessfulWarnings()
    {
        var result = EvaluateText(
            "%d|%u",
            ["9223372036854775808", "18446744073709551616"],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("9223372036854775807|18446744073709551615", Text(result.Output));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "warning: 9223372036854775808: Numerical result out of range",
                "warning: 18446744073709551616: Numerical result out of range"
            ],
            result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Fact]
    public void Evaluate_UsesStrftimeTimestampDialect()
    {
        var result = EvaluateText(
            "<%(%N)T>|<%(%+)T>|<%(%q)T>|<%(%J)T>|<%(%:z)T>|<%(%_J)T>"
                + "|<%(%EY)T>|<%(%OY)T>|<%(%Ed)T>|<%(%Od)T>",
            ["0", "0", "0", "0", "0", "0", "0", "0", "0", "0"],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(
            "<%N>|<%+>|<%q>|<%J>|<%:z>|<%_J>|<1970>|<%OY>|<%Ed>|<01>",
            Text(result.Output));
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Evaluate_UsesStrftimeDirectiveSpecificDefaultPadding()
    {
        var result = EvaluateText(
            "<%(%6a)T>|<%(%6Y)T>|<%(%6e)T>|<%(%6s)T>",
            ["0", "0", "0", "0"],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("<   Thu>|<001970>|<     1>|<     0>", Text(result.Output));
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Evaluate_BoundsStrftimeFieldConstruction()
    {
        var result = EvaluateText(
            "%(%1000000000Y)T",
            ["0"],
            DateTimeOffset.UnixEpoch);

        Assert.Empty(result.Output.ToArray());
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["shared memory budget exhausted"], result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Fact]
    public void Evaluate_EscapeWarningsDoNotFailFormatting()
    {
        var result = EvaluateText("\\x", [], DateTimeOffset.UnixEpoch);

        Assert.Equal("\\x", Text(result.Output));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("missing hex digit for \\x", result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Theory]
    [InlineData("%1048577s", "field width exceeds 1048576")]
    [InlineData("%.1048577d", "precision exceeds 1048576")]
    public void Evaluate_RejectsFieldsThatWouldRequireUnboundedMemory(string format, string diagnostic)
    {
        var result = EvaluateText(format, ["1"], DateTimeOffset.UnixEpoch);

        Assert.Empty(result.Output.ToArray());
        Assert.Equal(1, result.ExitCode);
        Assert.Equal([diagnostic], result.Diagnostics.Select(value => value.ToUtf8String()));
    }

    [Fact]
    public void Evaluate_ReturnsPercentNAssignments()
    {
        var result = EvaluateText("%s%n%s", ["a", "count", "bc"], DateTimeOffset.UnixEpoch);

        Assert.Equal("abc", Text(result.Output));
        Assert.Equal([new PrintfVariableAssignment("count", 1)], result.Assignments);
    }

    [Fact]
    public void Evaluate_PercentNCountsEscapedTemporaryOutputOnce()
    {
        var result = EvaluateText("%5b%n", ["a\\n", "count"], DateTimeOffset.UnixEpoch);

        Assert.Equal("   a\n", Text(result.Output));
        Assert.Equal([new PrintfVariableAssignment("count", 5)], result.Assignments);
    }

    [Fact]
    public void Evaluate_PercentNCountsWithinEachReusedFormat()
    {
        var result = EvaluateText(
            "%s%n",
            ["a", "first", "bc", "second"],
            DateTimeOffset.UnixEpoch);

        Assert.Equal("abc", Text(result.Output));
        Assert.Equal(
            [
                new PrintfVariableAssignment("first", 1),
                new PrintfVariableAssignment("second", 2)
            ],
            result.Assignments);
    }

    private static ReadOnlyMemory<byte> FormatText(string format, IReadOnlyList<string> arguments) =>
        PrintfFormatting.Format(ShellValue.FromText(format), arguments.Select(ShellValue.FromText).ToArray(), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.TextToolBytes));

    private static PrintfResult EvaluateText(
        string format, IReadOnlyList<string> arguments, DateTimeOffset timestamp) =>
        PrintfFormatting.Evaluate(ShellValue.FromText(format), arguments.Select(ShellValue.FromText).ToArray(), timestamp, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.TextToolBytes));

    private static string Text(ReadOnlyMemory<byte> content) => Encoding.UTF8.GetString(content.Span);
}
