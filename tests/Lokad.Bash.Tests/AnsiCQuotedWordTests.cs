using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class AnsiCQuotedWordTests
{
    [Theory]
    [InlineData("$'probe-f'", "probe-f")]
    [InlineData("$''", "")]
    [InlineData("$'a b\\tc'", "a b\tc")]
    [InlineData("$'\\a\\b\\e\\E\\f\\n\\r\\t\\v'", "\u0007\b\u001b\u001b\f\n\r\t\u000b")]
    [InlineData("$'\\\\\\\"\\'\\?'", "\\\"'?")]
    [InlineData("$'\\q\\z\\x\\u\\U'", "\\q\\z\\x\\u\\U")]
    [InlineData("$'\\x41F'", "AF")]
    [InlineData("$'\\1017\\0101'", "A7\b1")]
    [InlineData("$'\\u03bb\\U0001f642'", "λ🙂")]
    [InlineData("$'\\303\\251'", "é")]
    [InlineData("$'left\\0right'", "left")]
    [InlineData("$'left\\x00right'", "left")]
    [InlineData("$'left\\u0000right'", "left")]
    [InlineData("$'\\cA\\c?\\c@tail'", "\u0001\u007f")]
    [InlineData("$'\\c\\\\'", "\u001c")]
    [InlineData("$'tail\\c'", "tail\\c")]
    [InlineData("$'a\\\nb'", "a\\\nb")]
    [InlineData("$'a\rb'", "a\rb")]
    [InlineData("$\\\n'a\\tb'", "a\tb")]
    [InlineData("$'${missing}$(printf effect)`printf effect`$((1+2))'", "${missing}$(printf effect)`printf effect`$((1+2))")]
    [InlineData("$'\\c!\\c0\\c~'", "\u0001\u0010\u001e")]
    [InlineData("$'\\c\\''", "\u001c'")]
    public void Tokenizer_DecodesLiteralWithoutLosingSourceRange(string source, string expected)
    {
        var tokenizer = new Tokenizer(ShellSource.FromText(source + " tail"));

        var token = tokenizer.NextToken();
        Assert.True(token.TryGetWordToken(out var wordToken));
        var word = Assert.IsType<WordToken>(wordToken).Word;
        var part = Assert.Single(word.Parts);
        Assert.Equal(WordPartKind.SingleQuoted, part.Kind);
        Assert.Equal(expected, part.GetUtf8Text());
        Assert.Equal(ShellSource.FromText(source).Bytes.ToArray(), ShellSource.FromText(source + " tail").Bytes[token.Source].ToArray());
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var trailing));
        Assert.Equal("tail", Assert.Single(Assert.IsType<WordToken>(trailing).Word.Parts).GetUtf8Text());
    }

    [Theory]
    [InlineData("printf '<%q>\\n' pre$'a\\tb'post\n\n", "<$'prea\\tbpost'>\n")]
    [InlineData("printf '<%q>\\n' pre$'a\\0b'post\n\n", "<preapost>\n")]
    [InlineData("printf '<%q>\\n' '$''a\\tb'\n\n", "<\\$a\\\\tb>\n")]
    [InlineData("printf '<%q>\\n' \"$'a\\tb'\"\n\n", "<\\$\\'a\\\\tb\\'>\n")]
    [InlineData("printf '<%q>\\n' \\$'a\\tb'\n\n", "<\\$a\\\\tb>\n")]
    [InlineData("unset v; printf '<%q>\\n' \"${v:-$'a\\tb'}\"\n\n", "<$'a\\tb'>\n")]
    [InlineData("unset v; printf '<%q>\\n' ${v:-$'a\\'b}c'}\n\n", "<a\\'b\\}c>\n")]
    [InlineData("printf '<%q>\\n' \"$(printf '%s' $'a\\'b)c')\"\n\n", "<a\\'b\\)c>\n")]
    [InlineData("a=($'a\\tb' $'c\\'d'); printf '<%q>\\n' \"${a[@]}\"\n\n", "<$'a\\tb'>\n<c\\'d>\n")]
    [InlineData("case \"'\" in $'\\'') printf hit;; *) printf miss;; esac\n\n", "hit")]
    [InlineData("cat <<$'E\\x4fF'\nbody\nEOF\n\n\n", "body\n")]
    [InlineData("cat <<EOF\n$'a\\tb'\nEOF\n\n\n", "$'a\\tb'\n")]
    [InlineData("printf '%s' $'\\u03bb\\U0001f642'\n\n", "λ🙂")]
    public async Task Evaluation_PreservesQuotedContexts(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task Invocation_PassesDecodedSeparatorInsteadOfCommandLocalEscapeSpelling()
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse("sort -t $'\\t' -k2,2nr /file.tsv"));
        var operation = Assert.IsType<RunCommand>(await evaluation.GetNextStep());

        Assert.Equal(new[] { "-t", "\t", "-k2,2nr", "/file.tsv" }, operation.Command.Arguments.Select(argument => argument.ToUtf8String()));
        operation.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await evaluation.GetNextStep());
    }

    [Theory]
    [InlineData("$'a\\'")]
    [InlineData("$'a")]
    [InlineData("$'\\0unterminated")]
    public void Tokenizer_RejectsUnterminatedQuotesEvenAfterNul(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse("printf '%s' " + source));
    }

    [Theory]
    [InlineData("$\\\n'a\\\nb'\\\ntail", "$'a\\\nb'tail")]
    [InlineData("$'a\\'\\\nb'\\\ntail", "$'a\\'\\\nb'tail")]
    [InlineData("'$'\\\n'a\\\nb'", "'$''a\\\nb'")]
    public void LogicalText_PreservesQuotedLineContinuations(string source, string expected)
    {
        Assert.Equal(expected, ShellSourceCursor.RemoveLineContinuations(ShellSource.FromText(source)).ToUtf8String());
    }

    [Fact]
    public void NestedScanner_DoesNotTreatQuotedDelimitersOrSubstitutionsAsSyntax()
    {
        const string Source = "${value:-$'a\\'b}$(effect)'}tail";

        Assert.True(BracedParameterScanner.TryScan(ShellSource.FromText(Source), 0, out var closing, out var payload, out var command));
        Assert.Equal("tail", Source[(closing + 1)..]);
        Assert.Equal("value:-$'a\\'b}$(effect)'", payload.ToUtf8String());
        Assert.False(command);
    }

    [Theory]
    [InlineData("377", new byte[] { 0xff }, 3)]
    [InlineData("uD800", new byte[] { 0xed, 0xa0, 0x80 }, 5)]
    [InlineData("U00200000", new byte[] { 0xf8, 0x88, 0x80, 0x80, 0x80 }, 9)]
    [InlineData("cλ", new byte[] { 0x0e, 0xbb }, 2)]
    public void EscapePrimitive_PreservesNonUnicodeBytes(string source, byte[] expected, int consumed)
    {
        Span<byte> bytes = stackalloc byte[ShellEscapeDecoder.MaximumEscapeBytes];

        var result = ShellEscapeDecoder.Decode(ShellSource.FromText(source).AsSpan(), ShellEscapeMode.AnsiCQuoted, bytes);

        Assert.Equal(ShellEscapeStatus.Decoded, result.Status);
        // Original fixture lengths are UTF-16; the decoder now counts source bytes.
        Assert.Equal(ShellSource.FromText(source[..consumed]).Length, result.Consumed);
        Assert.Equal(expected, bytes[..result.Written].ToArray());
    }

    // Check native bytes without requiring a UTF-8 string conversion.
    [Theory]
    [InlineData("\\377", new byte[] { 0xff })]
    [InlineData("\\uD800", new byte[] { 0xed, 0xa0, 0x80 })]
    [InlineData("\\U00200000", new byte[] { 0xf8, 0x88, 0x80, 0x80, 0x80 })]
    [InlineData("\\cλ", new byte[] { 0x0e, 0xbb })]
    [InlineData("\\c🙂", new byte[] { 0x10, 0x9f, 0x99, 0x82 })]
    [InlineData("\\303\\251", new byte[] { 0xc3, 0xa9 })]
    [InlineData("\\0tail", new byte[] { })]
    [InlineData("\\U80000000", new byte[] { })]
    public void ValueDecoder_PreservesNativeBytes(string value, byte[] expected)
    {
        Assert.Equal(expected, ShellEscapeDecoder.DecodeAnsiCValue(ShellSource.FromText(value).AsSpan()).Bytes.ToArray());
    }
}
