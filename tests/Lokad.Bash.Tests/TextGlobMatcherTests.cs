using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class TextGlobMatcherTests
{
    [Theory]
    [InlineData("[]a]", "]", true)]
    [InlineData("[]a]", "a", true)]
    [InlineData("[]a]", "b", false)]
    [InlineData("[!]]", "]", false)]
    [InlineData("[!]]", "a", true)]
    [InlineData("[]]", "]", true)]
    [InlineData("[]-a]", "_", true)]
    [InlineData("[]", "[]", true)]
    public void BracketsTreatTheFirstClosingBracketAsAMember(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, TextGlobMatcher.IsMatch(pattern, text));
    }

    [Theory]
    [InlineData("*a", "*ba", true)]
    [InlineData("[a]", "[a]", false)]
    [InlineData("[a]", "a", true)]
    [InlineData("*[a]", "prefix[a]", false)]
    [InlineData("*b[ac]", "xbxba", true)]
    public void ActivePatternsPrecedeLiteralMatching(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, TextGlobMatcher.IsMatch(pattern, text));
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("*", "", true)]
    [InlineData("**a?", "prefixab", true)]
    [InlineData("*[a-c]", "prefixb", true)]
    [InlineData("*[a-c]", "prefixd", false)]
    [InlineData("[!a-c]", "d", true)]
    [InlineData("[abc", "[abc", true)]
    [InlineData("*", "dir/file", true)]
    public void ImplicitAndExplicitActiveMasksHaveTheSameSemantics(string pattern, string text, bool expected)
    {
        var activity = new byte[pattern.Length];
        activity.AsSpan().Fill(GlobMask.Active);
        var mask = new GlobMaskSpan(activity);

        Assert.Equal(expected, TextGlobMatcher.IsMatch(pattern, text));
        Assert.Equal(expected, TextGlobMatcher.IsMatch(pattern, in mask, text));
    }

    [Theory]
    [InlineData("*?", "01", "*a", true)]
    [InlineData("*?", "01", "ba", false)]
    [InlineData("*?", "10", "prefix?", true)]
    [InlineData("[a]", "011", "[a]", true)]
    [InlineData("[a]", "011", "a", false)]
    [InlineData("[a]", "110", "[a]", true)]
    [InlineData("[a]", "110", "a", false)]
    [InlineData("[!a]", "1011", "!", true)]
    [InlineData("[!a]", "1011", "b", false)]
    [InlineData("[a-c]", "11011", "-", true)]
    [InlineData("[a-c]", "11011", "b", false)]
    [InlineData("[a]b]", "11011", "]", true)]
    public void QuotedSyntaxRetainsItsLiteralMeaning(string pattern, string activity, string text, bool expected)
    {
        var mask = new GlobMaskSpan(activity.Select(character => (byte)(character - '0')).ToArray());

        Assert.Equal(expected, TextGlobMatcher.IsMatch(pattern, in mask, text));
    }

    [Theory]
    [InlineData("?", "é", true, false)]
    [InlineData("??", "é", false, true)]
    [InlineData("??", "😀", true, false)]
    [InlineData("[é]", "é", true, false)]
    public void TextAndByteWildcardsRetainTheirDifferentCharacterUnits(
        string pattern, string text, bool textMatches, bool bytesMatch)
    {
        Assert.Equal(textMatches, TextGlobMatcher.IsMatch(pattern, text));
        Assert.Equal(bytesMatch, ByteGlobMatcher.IsMatch(Encoding.UTF8.GetBytes(pattern), Encoding.UTF8.GetBytes(text)));
    }

    [Fact(Skip = "Intermittent allocation assertion failure in full-suite runs; passes in isolation. Needs reliable allocation measurement.")]
    public void LongPlainPatternsMatchWithoutAllocating()
    {
        var text = new string('a', 4096);
        var pattern = text + "*";
        Assert.True(TextGlobMatcher.IsMatch(pattern, text));
        var matched = true;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++)
            matched &= TextGlobMatcher.IsMatch(pattern, text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(matched);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData("?", "é", true, 2)]
    [InlineData("?", "🙂x", false, 4)]
    [InlineData("[🙂]", "🙂x", false, 4)]
    [InlineData("[é-ê]", "ê", true, 2)]
    [InlineData("a*b", "abbxxb!", false, 6)]
    [InlineData("a*b", "abbxxb!", true, -1)]
    [InlineData("*", "🙂é", false, 6)]
    [InlineData("?*?", "🙂é", true, 6)]
    [InlineData("[!]]", "]x", false, -1)]
    [InlineData("[]]", "]x", false, 1)]
    [InlineData("", "abc", false, 0)]
    [InlineData("", "abc", true, -1)]
    [InlineData("*", "", true, 0)]
    public void Utf8MatchingReturnsTheLongestCharacterAlignedPrefix(string pattern, string value, bool requireEnd, int expectedLength)
    {
        var mask = default(GlobMaskSpan);
        var work = 10000L;
        var result = TextGlobMatcher.MatchUtf8(Encoding.UTF8.GetBytes(pattern), in mask, Encoding.UTF8.GetBytes(value),
            requireEnd, ref work, CancellationToken.None, out var length);

        Assert.Equal(expectedLength < 0 ? GlobMatchResult.NoMatch : GlobMatchResult.Match, result);
        Assert.Equal(expectedLength, length);
    }

    [Fact]
    public void Utf8MatchingDistinguishesMalformedBytesFromReplacementCharacters()
    {
        var mask = default(GlobMaskSpan);
        var work = 100L;
        Assert.Equal(GlobMatchResult.Match,
            TextGlobMatcher.MatchUtf8([0xff], in mask, [0xff], true, ref work, CancellationToken.None, out _));
        Assert.Equal(GlobMatchResult.NoMatch,
            TextGlobMatcher.MatchUtf8("�"u8, in mask, [0xff], true, ref work, CancellationToken.None, out _));
    }

    [Theory]
    [InlineData("*z", "aaaaaaaa")]
    [InlineData("[abcdefgh]", "z")]
    [InlineData("********", "")]
    public void Utf8MatchingBoundsBacktrackingAndPatternScanning(string pattern, string value)
    {
        var mask = default(GlobMaskSpan);
        var work = 3L;
        Assert.Equal(GlobMatchResult.LimitExceeded,
            TextGlobMatcher.MatchUtf8(Encoding.UTF8.GetBytes(pattern), in mask, Encoding.UTF8.GetBytes(value),
                false, ref work, CancellationToken.None, out _));
    }

    [Fact]
    public void Utf8MatchingObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
        {
            var mask = default(GlobMaskSpan);
            var work = 100L;
            TextGlobMatcher.MatchUtf8("*"u8, in mask, "value"u8, true, ref work, cancellation.Token, out _);
        });
    }
}
