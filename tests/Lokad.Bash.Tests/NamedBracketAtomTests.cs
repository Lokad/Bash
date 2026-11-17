using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class NamedBracketAtomTests
{
    // C membership is independently pinned by both GNU references; NUL is a direct matcher control.
    public static IEnumerable<object[]> FixedClassMembers()
    {
        yield return ["alnum", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"];
        yield return ["alpha", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"];
        yield return ["ascii", "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000b\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~\u007f"];
        yield return ["blank", "\t "];
        yield return ["cntrl", "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000b\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f\u007f"];
        yield return ["digit", "0123456789"];
        yield return ["graph", "!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~"];
        yield return ["lower", "abcdefghijklmnopqrstuvwxyz"];
        yield return ["print", " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~"];
        yield return ["punct", "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~"];
        yield return ["space", "\t\n\u000b\f\r "];
        yield return ["upper", "ABCDEFGHIJKLMNOPQRSTUVWXYZ"];
        yield return ["word", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz"];
        yield return ["xdigit", "0123456789ABCDEFabcdef"];
    }

    [Theory]
    [MemberData(nameof(FixedClassMembers))]
    public void NamedClassesUseTheDeclaredAsciiSetAcrossTextAndByteOverloads(string name, string members)
    {
        var pattern = "[[:" + name + ":]]";
        var encoded = Encoding.ASCII.GetBytes(pattern);
        var mask = default(GlobMaskSpan);
        Span<char> character = stackalloc char[1];
        Span<byte> octet = stackalloc byte[1];
        for (var value = 0; value <= 255; value++)
        {
            character[0] = (char)value;
            octet[0] = (byte)value;
            var expected = members.AsSpan().Contains((char)value);
            Assert.Equal(expected, TextGlobMatcher.IsMatch(pattern, character));
            Assert.Equal(expected ? GlobMatchResult.Match : GlobMatchResult.NoMatch,
                TextGlobMatcher.MatchUtf8(encoded, in mask, octet, true, CancellationToken.None, out var length));
            Assert.Equal(expected ? 1 : -1, length);
        }
    }

    [Theory]
    [InlineData("[[:digit:]]", "5", true)]
    [InlineData("[[:digit:]]", "d]", false)]
    [InlineData("[[:digit:][:alpha:]]", "a", true)]
    [InlineData("[![:unknown:]]", "a", true)]
    [InlineData("[[:unknown:]a]", "a", true)]
    [InlineData("[[:unknown:]]", "[[:unknown:]]", false)]
    [InlineData("[[.a.]]", "a", true)]
    [InlineData("[[=a=]]", "a", true)]
    [InlineData("[[.ch.]]", "ch", false)]
    [InlineData("[![.ch.]]", "a", true)]
    [InlineData("[[.a.]-[.c.]]", "b", true)]
    [InlineData("[a-[.c.]]", "b", true)]
    [InlineData("[[=a=]-[=c=]]", "b", false)]
    [InlineData("[[=a=]-[=c=]]", "-", true)]
    [InlineData("[a-[:digit:]]", "d]", true)]
    [InlineData("[[:digit:]-[:alpha:]]", "-", true)]
    [InlineData("[[.ch.]-z]", "z", false)]
    [InlineData("[[:digit]", "d", true)]
    [InlineData("[[:digit]", "[", false)]
    [InlineData("[[:digit]]", "[]", false)]
    [InlineData("[[:digit:]", "[d", true)]
    [InlineData("[[.a]", "[a", true)]
    public void NamedAtomsRangesAndMalformedFallbackHaveIndependentExpectations(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, TextGlobMatcher.IsMatch(pattern, value));
        var mask = default(GlobMaskSpan);
        Assert.Equal(expected ? GlobMatchResult.Match : GlobMatchResult.NoMatch,
            TextGlobMatcher.MatchUtf8(Encoding.UTF8.GetBytes(pattern), in mask, Encoding.UTF8.GetBytes(value),
                true, CancellationToken.None, out _));
    }

    [Theory]
    [InlineData("[[:digit:]]", "11111011111", "5", true)]
    [InlineData("[[:digit:]]", "11111111011", "5", true)]
    [InlineData("[[:digit:]]", "10111111111", "d]", true)]
    [InlineData("[[:digit:]]", "11011111111", "5", false)]
    [InlineData("[[:digit:]]", "11111111101", "]", true)]
    [InlineData("[[:digit:]]", "11111111101", "[", false)]
    [InlineData("[[.a.]]", "1110111", "a]", false)]
    [InlineData("[[.a.]]", "1111011", "a]", false)]
    [InlineData("[[=a=]]", "1110111", "a]", true)]
    [InlineData("[[=a=]]", "1111101", "a", true)]
    public void QuotingKeepsTheDistinctClassSymbolAndEquivalenceRules(string pattern, string activity, string value, bool expected)
    {
        var mask = new GlobMaskSpan(activity.Select(ch => (byte)(ch - '0')).ToArray());
        Assert.Equal(expected, TextGlobMatcher.IsMatch(pattern, in mask, value));
        Assert.Equal(expected ? GlobMatchResult.Match : GlobMatchResult.NoMatch,
            TextGlobMatcher.MatchUtf8(Encoding.ASCII.GetBytes(pattern), in mask, Encoding.ASCII.GetBytes(value),
                true, CancellationToken.None, out _));
    }

    [Theory]
    [InlineData("[[:alpha:]]", "é", false)]
    [InlineData("[![:alpha:]]", "é", true)]
    [InlineData("[![:ascii:]]", "é", true)]
    [InlineData("[[.é.]]", "é", true)]
    [InlineData("[[=é=]]", "é", true)]
    [InlineData("[[.😀.]]", "😀", true)]
    [InlineData("[[.é.]-[.ê.]]", "ê", true)]
    public void Utf8ScalarsRetainFixedClassMembershipAndSingleSymbolBoundaries(string pattern, string value, bool expected)
    {
        var mask = default(GlobMaskSpan);
        var text = Encoding.UTF8.GetBytes(value);
        Assert.Equal(expected ? GlobMatchResult.Match : GlobMatchResult.NoMatch,
            TextGlobMatcher.MatchUtf8(Encoding.UTF8.GetBytes(pattern), in mask, text, true, CancellationToken.None, out var length));
        Assert.Equal(expected ? text.Length : -1, length);
    }

    [Theory]
    [InlineData("[[.é.]]")]
    [InlineData("[[=é=]]")]
    public void QuotingOneByteOfAScalarDoesNotCreateAnUnquotedSymbol(string pattern)
    {
        var bytes = Encoding.UTF8.GetBytes(pattern);
        var activity = new byte[bytes.Length];
        activity.AsSpan().Fill(GlobMask.Active);
        activity[4] = 0;
        var mask = new GlobMaskSpan(activity);
        Assert.Equal(GlobMatchResult.NoMatch,
            TextGlobMatcher.MatchUtf8(bytes, in mask, "é"u8, true, CancellationToken.None, out _));
    }

    [Theory]
    [InlineData("[[.😀.]]")]
    [InlineData("[[=😀=]]")]
    public void TextOverloadsKeepTheirExistingUtf16MatchingUnits(string pattern)
    {
        Assert.False(TextGlobMatcher.IsMatch(pattern, "😀"));
    }

    [Fact]
    public void RepeatedUnclosedBracketsRemainLiteralInBothCharacterProfiles()
    {
        var pattern = new string('[', 32_768);
        Assert.True(TextGlobMatcher.IsMatch(pattern, pattern));
        foreach (var prefix in new[] { new byte[] { 0xc3, 0xa9 }, new byte[] { 0xff } })
        {
            var bytes = prefix.Concat(Encoding.ASCII.GetBytes(pattern)).ToArray();
            var mask = default(GlobMaskSpan);
            Assert.Equal(GlobMatchResult.Match,
                TextGlobMatcher.MatchUtf8(bytes, in mask, bytes, true, CancellationToken.None, out var length));
            Assert.Equal(bytes.Length, length);
        }
    }

    [Fact]
    public void AQuotedClosingBracketDoesNotEndTheLiteralFallbackScan()
    {
        var pattern = new string('[', 4096) + "]";
        var bytes = Encoding.ASCII.GetBytes(pattern);
        foreach (var activeClose in new[] { false, true })
        {
            var activity = new byte[bytes.Length];
            activity.AsSpan().Fill(GlobMask.Active);
            activity[^1] = activeClose ? GlobMask.Active : (byte)0;
            var mask = new GlobMaskSpan(activity);
            Assert.Equal(!activeClose, TextGlobMatcher.IsMatch(pattern, in mask, pattern));
            Assert.Equal(activeClose, TextGlobMatcher.IsMatch(pattern, in mask, "["));
            Assert.Equal(activeClose ? GlobMatchResult.NoMatch : GlobMatchResult.Match,
                TextGlobMatcher.MatchUtf8(bytes, in mask, bytes, true, CancellationToken.None, out _));
        }
    }
}
