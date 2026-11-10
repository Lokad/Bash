using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteGlobMatcherTests
{
    [Theory]
    [InlineData("*.cs", "file.cs", true)]
    [InlineData("*.cs", "src/file.cs", false)]
    [InlineData("**/*.cs", "src/file.cs", true)]
    [InlineData("src/**/file?.[ch]", "src/generated/file1.c", true)]
    [InlineData("src/**/file?.[ch]", "src/generated/file10.c", false)]
    public void RequireGlobStar_SeparatesPathSegments(
        string pattern,
        string text,
        bool expected)
    {
        var compiled = ByteGlobMatcher.Compile(
            Encoding.UTF8.GetBytes(pattern),
            ByteGlobSeparatorMode.RequireGlobStar);

        var matched = ByteGlobMatcher.IsMatch(
            compiled,
            Encoding.UTF8.GetBytes(text),
            ByteGlobCaseSensitivity.Sensitive,
            ByteGlobSeparatorMode.RequireGlobStar);

        Assert.Equal(expected, matched);
    }

    [Theory]
    [InlineData("[a-c].txt", "B.TXT", true)]
    [InlineData("[!a-c].txt", "D.txt", true)]
    [InlineData("[!a-c].txt", "b.txt", false)]
    public void MatchSeparators_AppliesAsciiCaseFoldingAndBrackets(
        string pattern,
        string text,
        bool expected)
    {
        var compiled = ByteGlobMatcher.Compile(
            Encoding.UTF8.GetBytes(pattern),
            ByteGlobSeparatorMode.MatchSeparators);

        var matched = ByteGlobMatcher.IsMatch(
            compiled,
            Encoding.UTF8.GetBytes(text),
            ByteGlobCaseSensitivity.AsciiInsensitive,
            ByteGlobSeparatorMode.MatchSeparators);

        Assert.Equal(expected, matched);
    }

    [Fact]
    public void CompiledPattern_ExposesPruningMetadata()
    {
        var compiled = ByteGlobMatcher.Compile(
            ".hidden/**"u8.ToArray(),
            ByteGlobSeparatorMode.RequireGlobStarPrefix);

        Assert.True(compiled.StartsWithDot);
        Assert.False(compiled.IsEmpty);
        Assert.False(compiled.MatchesSeparators);
    }
}
