using System.Collections.Generic;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class TokenizerCommandSubstitutionTests
{
    [Theory]
    [InlineData('$')]
    [InlineData('"')]
    [InlineData('\\')]
    public void Tokenize_DoubleQuotedEscapes_PreserveLiteralParts(char escaped)
    {
        var words = TokenizeWords("echo \"before\\" + escaped + "after\"");

        Assert.Equal(2, words.Count);
        AssertParts(
            words[1],
            (WordPartKind.DoubleQuoted, "before"),
            (WordPartKind.Escaped, escaped.ToString()),
            (WordPartKind.DoubleQuoted, "after"));
    }

    [Fact]
    public void Tokenize_CommandSubstitutionInDoubleQuotes_WithInnerQuotes_SplitsParts()
    {
        var words = TokenizeWords("echo \"$d $(find \"$d\" -maxdepth 1 -type d | wc -l)\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.DoubleQuoted, "$d "),
            (WordPartKind.CommandSubstitutionQuoted, "find \"$d\" -maxdepth 1 -type d | wc -l"));
    }

    [Fact]
    public void Tokenize_CommandSubstitution_RespectsSingleQuotedCloseParen()
    {
        var words = TokenizeWords("echo $(echo ')' foo)");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitution, "echo ')' foo"));
    }

    [Fact]
    public void Tokenize_CommandSubstitution_RespectsDoubleQuotedCloseParen()
    {
        var words = TokenizeWords("echo $(echo \"\\)\")");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitution, "echo \"\\)\""));
    }

    [Fact]
    public void Tokenize_CommandSubstitution_AllowsNested()
    {
        var words = TokenizeWords("echo $(echo $(printf \")\"))");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitution, "echo $(printf \")\")"));
    }

    [Fact]
    public void Tokenize_CommandSubstitutionInDoubleQuotes_AllowsSingleQuotes()
    {
        var words = TokenizeWords("echo \"x $(echo 'y') z\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.DoubleQuoted, "x "),
            (WordPartKind.CommandSubstitutionQuoted, "echo 'y'"),
            (WordPartKind.DoubleQuoted, " z"));
    }

    [Fact]
    public void Tokenize_CommandSubstitution_AllowsFunctionDefinitionParens()
    {
        var words = TokenizeWords("echo \"$(f() { echo hi; }; f)\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitutionQuoted, "f() { echo hi; }; f"));
    }

    [Fact]
    public void Tokenize_CommandSubstitution_AllowsNestedSubshellParens()
    {
        var words = TokenizeWords("echo \"$( (echo hi) )\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitutionQuoted, " (echo hi) "));
    }

    [Fact]
    public void Tokenize_CommandSubstitution_AllowsNestedSubstitutionWithSubshellParens()
    {
        var words = TokenizeWords("echo \"$(echo $( (echo hi) ))\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitutionQuoted, "echo $( (echo hi) )"));
    }

    [Fact]
    public void Tokenize_CommandSubstitution_AllowsLeadingTerminatorBeforeNestedSubshellParens()
    {
        var words = TokenizeWords("echo \"$(\n(echo hi)\n)\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitutionQuoted, "\n(echo hi)\n"));
    }

    [Fact]
    public void Tokenize_CommandSubstitution_AllowsCasePatternParens()
    {
        var words = TokenizeWords("echo \"$(case x in x) echo hi ;; esac)\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitutionQuoted, "case x in x) echo hi ;; esac"));
    }

    [Fact]
    public void Tokenize_LegacyBacktickCommandSubstitution_SplitsParts()
    {
        var words = TokenizeWords("echo a`echo hi`b");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.Unquoted, "a"),
            (WordPartKind.CommandSubstitution, "echo hi"),
            (WordPartKind.Unquoted, "b"));
    }

    [Fact]
    public void Tokenize_LegacyBacktickCommandSubstitutionInDoubleQuotes_SplitsParts()
    {
        var words = TokenizeWords("echo \"a `echo hi` b\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.DoubleQuoted, "a "),
            (WordPartKind.CommandSubstitutionQuoted, "echo hi"),
            (WordPartKind.DoubleQuoted, " b"));
    }

    [Fact]
    public void Tokenize_LegacyBacktickCommandSubstitution_AllowsEscapedBacktickInPayload()
    {
        var words = TokenizeWords("echo `printf '\\`'`");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.CommandSubstitution, "printf '`'"));
    }

    [Fact]
    public void Tokenize_LegacyBacktickCommandSubstitutionInsideBracedParameter_StaysInParameterPart()
    {
        var words = TokenizeWords("echo ${missing:-`echo hi`}");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.Unquoted, "${missing:-`echo hi`}"));
    }

    [Fact]
    public void Tokenize_EscapedBacktickInDoubleQuotes_IsLiteral()
    {
        var words = TokenizeWords("echo \"\\`echo hi\\`\"");

        Assert.Equal(2, words.Count);
        Assert.Equal("echo", WordText(words[0]));
        AssertParts(
            words[1],
            (WordPartKind.Escaped, "`"),
            (WordPartKind.DoubleQuoted, "echo hi"),
            (WordPartKind.Escaped, "`"));
    }

    private static List<Word> TokenizeWords(string script)
    {
        var tokenizer = new Tokenizer(ShellSource.FromText(script));
        var words = new List<Word>();
        while (true)
        {
            var token = tokenizer.NextToken();
            if (token.Kind == TokenKind.EndOfInput)
                return words;

            if (token.TryGetWordToken(out var wordToken))
                words.Add(wordToken.Word);
        }
    }

    private static string WordText(Word word)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (var part in word.Parts)
        {
            buffer.Append(part.GetUtf8Text());
        }

        return buffer.ToString();
    }

    private static void AssertParts(Word word, params (WordPartKind kind, string content)[] parts)
    {
        Assert.Equal(parts.Length, word.Parts.Count);
        for (var index = 0; index < parts.Length; index++)
        {
            Assert.Equal(parts[index].kind, word.Parts[index].Kind);
            Assert.Equal(parts[index].content, word.Parts[index].GetUtf8Text());
        }
    }
}
