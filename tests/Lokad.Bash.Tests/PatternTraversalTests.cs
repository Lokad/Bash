using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class PatternTraversalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PatternProjectionPreservesByteActivityAcrossSegments(bool quotedContent)
    {
        var word = new ExpandedWord([
            new(ShellValue.FromBytes(new byte[] { 0xc3 }), isGlobQuoted: false),
            new(ShellValue.Empty, isGlobQuoted: true),
            new(ShellValue.FromBytes(new byte[] { 0xa9, (byte)'*' }), isGlobQuoted: quotedContent),
            new(ShellValue.FromBytes(new byte[] { 0xff, (byte)'?' }), isGlobQuoted: false)
        ]);
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var (value, mask) = ExpandedWord.JoinPatternWithSpaces([word], memory);
            Assert.Equal<byte>([0xc3, 0xa9, (byte)'*', 0xff, (byte)'?'], value.Bytes.ToArray());
            Assert.Equal<byte>(quotedContent ? [1, 0, 0, 1, 1] : [], mask.Memory.ToArray());
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void PatternJoinMarksSeparatorsActiveAroundAnEmptyField()
    {
        ExpandedWord[] fields = [
            new([new(ShellValue.FromText("q"), isGlobQuoted: false)]),
            new([new(ShellValue.FromBytes(new byte[] { 0xff, (byte)'*' }), isGlobQuoted: true)]),
            new([], isEmptyAggregateField: true),
            new([new(ShellValue.FromText("?"), isGlobQuoted: false)])
        ];
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var (value, mask) = ExpandedWord.JoinPatternWithSpaces(fields, memory);
            Assert.Equal<byte>([(byte)'q', (byte)' ', 0xff, (byte)'*', (byte)' ', (byte)' ', (byte)'?'],
                value.Bytes.ToArray());
            Assert.Equal<byte>([1, 1, 0, 0, 1, 1, 1], mask.Memory.ToArray());
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("[[:digit:]]tail", new[] { 0, 11, 12, 13, 14 })]
    [InlineData("[[:digit:][:alpha:]]z", new[] { 0, 20 })]
    [InlineData("[[.a.]-[.c.]]x", new[] { 0, 13 })]
    [InlineData("[[=a=]-[=c=]]x", new[] { 0, 13 })]
    [InlineData("[[:unknown:]]", new[] { 0 })]
    [InlineData("[[.ch.]]", new[] { 0 })]
    [InlineData("[[.a]", new[] { 0, 1 })]
    [InlineData("[[:digit:]", new[] { 0, 1 })]
    public void NamedBracketAtomsHaveIndependentPhysicalBoundaries(string pattern, int[] expected)
    {
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var mask = default(GlobMaskSpan);
            var bytes = Encoding.UTF8.GetBytes(pattern);
            foreach (var byteCharacters in new[] { false, true })
                Assert.Equal(expected, TextGlobMatcher.PrepareUtf8Atoms(bytes, in mask, byteCharacters, memory, CancellationToken.None).ToArray());
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void NamedAtomsKeepCharacterAlignedSearchPrefixAndBothSuffixDirections()
    {
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var mask = default(GlobMaskSpan);
            var pattern = ShellPattern.FromUnescaped(ShellValue.FromText("[[:digit:]]*"), new GlobMask(ReadOnlyMemory<byte>.Empty), memory, CancellationToken.None);
            Assert.True(pattern.TryMatchPrefix("55é"u8, true, CancellationToken.None, out var length));
            Assert.Equal(1, length);
            Assert.True(pattern.TryMatchPrefix("55é"u8, false, CancellationToken.None, out length));
            Assert.Equal(4, length);
            Assert.Equal(GlobMatchResult.Match,
                TextGlobMatcher.FindUtf8("[[:digit:]]*"u8, in mask, "é5x5z"u8, false, memory.Workspace,
                    CancellationToken.None, out var start, out length));
            Assert.Equal(2, start);
            Assert.Equal(4, length);
            Assert.True(pattern.TryMatchSuffix("é5x5z"u8, false, CancellationToken.None, out start));
            Assert.Equal(2, start);
            var retained = governor.ReservedBytes;
            Assert.True(pattern.TryMatchSuffix("é5x5z"u8, true, CancellationToken.None, out start));
            Assert.Equal(4, start);
            Assert.Equal(retained, governor.ReservedBytes);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void SearchAndSuffixTraversalMatchExhaustiveCandidateSelection()
    {
        var texts = Words(["a", "b"], 5).ToArray();
        var patterns = Words(["a", "b", "?", "*", "[ab]", "[!a]"], 3)
            .Concat(["[", "[]", "[]]", "[]a]", "[a-b]", "[^b]", "*[a-b]*a"]);
        var memory = new EvaluationMemory(new MemoryGovernor(1024 * 1024));
        try
        {
            foreach (var pattern in patterns)
            {
                var encoded = Encoding.UTF8.GetBytes(pattern);
                var mask = default(GlobMaskSpan);
                var atoms = TextGlobMatcher.PrepareUtf8Atoms(encoded, in mask, byteCharacters: true, memory, CancellationToken.None);
                foreach (var text in texts)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    var expectedStart = -1;
                    var expectedLength = -1;
                    var longestSuffixStart = -1;
                    var shortestSuffixStart = -1;
                    for (var start = 0; start <= bytes.Length; start++)
                    {
                        for (var end = bytes.Length; end >= start; end--)
                        {
                            if (TextGlobMatcher.MatchUtf8(encoded, in mask, bytes.AsSpan(start, end - start),
                                    requireEnd: true, byteCharacters: true, memory.Workspace,
                                    CancellationToken.None, out _) != GlobMatchResult.Match)
                                continue;
                            if (expectedStart < 0)
                            {
                                expectedStart = start;
                                expectedLength = end - start;
                            }
                            if (end == bytes.Length)
                            {
                                if (longestSuffixStart < 0) longestSuffixStart = start;
                                shortestSuffixStart = start;
                            }
                        }
                    }
                    var found = TextGlobMatcher.FindUtf8(encoded, in mask, bytes, byteCharacters: true,
                        memory.Workspace, CancellationToken.None, out var actualStart, out var actualLength);
                    Assert.True((expectedStart >= 0) == (found == GlobMatchResult.Match), $"search {pattern} / {text}");
                    if (expectedStart >= 0)
                    {
                        Assert.Equal(expectedStart, actualStart);
                        Assert.Equal(expectedLength, actualLength);
                    }
                    foreach (var shortest in new[] { false, true })
                    {
                        var expected = shortest ? shortestSuffixStart : longestSuffixStart;
                        var matched = TextGlobMatcher.MatchUtf8Suffix(encoded, in mask, atoms.Span, bytes,
                            shortest, byteCharacters: true, memory.Workspace, CancellationToken.None, out var suffixLength);
                        Assert.True((expected >= 0) == (matched == GlobMatchResult.Match), $"suffix {shortest} {pattern} / {text}");
                        if (expected >= 0) Assert.Equal(expected, bytes.Length - suffixLength);
                    }
                }
            }
        }
        finally { memory.Finish(retainVariables: false); }

        static IEnumerable<string> Words(string[] atoms, int maximum)
        {
            var layer = new[] { "" };
            yield return "";
            for (var depth = 0; depth < maximum; depth++)
            {
                layer = layer.SelectMany(prefix => atoms.Select(atom => prefix + atom)).ToArray();
                foreach (var value in layer) yield return value;
            }
        }
    }

    [Theory]
    [InlineData("5")]
    [InlineData("j")]
    [InlineData("x")]
    [InlineData("k")]
    [InlineData("Ω")]
    [InlineData("ê")]
    [InlineData("😃")]
    public void RetriedBracketRetainsMembershipAndBoundsAcrossTraversals(string member)
    {
        const string bracket = "[[:digit:][=j=][.x.]a-gd-kΑ-ΩΒ-Δé-ê😀-😅]";
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        var mask = new GlobMask(ReadOnlyMemory<byte>.Empty);
        var before = new string('!', 12) + "☀";
        var forward = Encoding.UTF8.GetBytes(before + member);
        var reverse = Encoding.UTF8.GetBytes(member + before);
        try
        {
            var headValue = ShellValue.FromText("*" + bracket);
            ExpandedWord[] words = [new([new(headValue, isGlobQuoted: false)])];
            Assert.True(ShellPattern.MatchesWords(words, forward, memory, CancellationToken.None));
            Assert.False(ShellPattern.MatchesWords(words, Encoding.UTF8.GetBytes(before), memory, CancellationToken.None));
            var head = ShellPattern.FromUnescaped(headValue, mask, memory, CancellationToken.None);
            Assert.True(head.TryMatchPrefix(forward, shortest: true, CancellationToken.None, out var length));
            Assert.Equal(forward.Length, length);

            var tail = Prepare(bracket + "*");
            Assert.True(tail.TryMatchSuffix(reverse, shortest: true, CancellationToken.None, out var start));
            Assert.Equal(0, start);

            var search = Prepare(bracket);
            var reservation = memory.Workspace.Bytes;
            Assert.True(search.TryFind(forward, byteCharacters: false, CancellationToken.None, out start, out length));
            Assert.Equal(Encoding.UTF8.GetByteCount(before), start);
            Assert.Equal(Encoding.UTF8.GetByteCount(member), length);
            Assert.Equal(reservation, memory.Workspace.Bytes);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);

        ShellPattern Prepare(string pattern) =>
            ShellPattern.FromUnescaped(ShellValue.FromText(pattern), mask, memory, CancellationToken.None);
    }

    [Fact]
    public void RetriedBracketsKeepIndependentQuotedAndNegatedMembership()
    {
        var pattern = "*[a-cxyzxyzxyzxyzxyz][![:digit:]xyzxyzxyzxyz]z"u8;
        var activity = new byte[pattern.Length];
        activity.AsSpan().Fill(GlobMask.Active);
        activity[pattern.IndexOf((byte)'-')] = 0;
        var mask = new GlobMask(activity).AsSpan();
        Assert.Equal(GlobMatchResult.Match,
            TextGlobMatcher.MatchUtf8(pattern, in mask, "aaaaaaaaaaaa-agz"u8, true, CancellationToken.None, out _));
        Assert.Equal(GlobMatchResult.NoMatch,
            TextGlobMatcher.MatchUtf8(pattern, in mask, "aaaaaaaaaaaabgz"u8, true, CancellationToken.None, out _));
        Assert.Equal(GlobMatchResult.NoMatch,
            TextGlobMatcher.MatchUtf8(pattern, in mask, "aaaaaaaaaaaa-5z"u8, true, CancellationToken.None, out _));
    }

    [Fact]
    public void RetriedBracketMatchesRawBytesInMalformedValues()
    {
        var pattern = ("*[abcdefghijklmno"u8.ToArray()).Concat(new byte[] { 0xff, (byte)']' }).ToArray();
        var text = ("!!!!!!!!!!!!"u8.ToArray()).Append((byte)0xff).ToArray();
        var mask = default(GlobMaskSpan);
        Assert.Equal(GlobMatchResult.Match,
            TextGlobMatcher.MatchUtf8(pattern, in mask, text, true, CancellationToken.None, out var length));
        Assert.Equal(text.Length, length);
    }

    [Fact]
    public void SingleComparisonBorrowsAnUnescapedLiteralPattern()
    {
        byte[] bytes = [0xff, (byte)'x'];
        ExpandedWord[] words = [new([new(ShellValue.FromBytes(bytes), isGlobQuoted: false)])];
        var governor = new MemoryGovernor(0);
        var memory = new EvaluationMemory(governor);
        try
        {
            Assert.True(ShellPattern.MatchesWords(words, bytes, memory, CancellationToken.None));
            Assert.False(ShellPattern.MatchesWords(words, [0xfe, (byte)'x'], memory, CancellationToken.None));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetriedBracketScratchRefundsOnRefusalOrCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var governor = new MemoryGovernor(cancel ? 128 * 1024 : 8 * 1024);
        var admission = new CountingMemoryBudget(governor);
        var memory = new EvaluationMemory(admission);
        try
        {
            var pattern = ShellValue.FromText("*[" + new string('a', 4096) + "z]");
            ExpandedWord[] words = [new([new(pattern, isGlobQuoted: false)])];
            var baseline = memory.Workspace.Bytes;
            Assert.True(ShellPattern.MatchesWords(words, "z"u8, memory, CancellationToken.None));
            Assert.Equal(baseline, memory.Workspace.Bytes);
            if (cancel)
            {
                admission.AdmissionSucceeded += cancellation.Cancel;
                var failure = Assert.ThrowsAny<OperationCanceledException>(() =>
                    ShellPattern.MatchesWords(words, "!!!z"u8, memory, cancellation.Token));
                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
                Assert.Throws<MemoryBudgetExceededException>(() =>
                    ShellPattern.MatchesWords(words, "!!!z"u8, memory, cancellation.Token));
            Assert.Equal(baseline, memory.Workspace.Bytes);
            Assert.True(ShellPattern.MatchesWords(words, "z"u8, memory, CancellationToken.None));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("*b")]
    [InlineData("b*")]
    [InlineData("zz")]
    public async Task LargeNoMatchRemovalAndReplacementPreserveTheValue(string pattern)
    {
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var script = "x='" + new string('a', 65_536) + "'; pattern='" + pattern + "'; "
            + "head=${x#$pattern}; tail=${x%$pattern}; long=${x%%$pattern}; first=${x/$pattern/X}; all=${x//$pattern/X}; "
            + "printf '%s:%s:%s:%s:%s' \"${#head}\" \"${#tail}\" \"${#long}\" \"${#first}\" \"${#all}\"";
        var result = await host.RunAsync(script, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("65536:65536:65536:65536:65536", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void SuffixAtomPreparationIsAdmittedAndReusedWithoutReversingTheInput()
    {
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        var bytes = Encoding.UTF8.GetBytes(new string('a', 262_144) + "b");
        try
        {
            var pattern = ShellPattern.FromUnescaped(ShellValue.FromText("a*b"), new GlobMask(ReadOnlyMemory<byte>.Empty), memory, CancellationToken.None);
            Assert.True(pattern.TryMatchSuffix(bytes, shortest: false, CancellationToken.None, out var start));
            Assert.Equal(0, start);
            var reserved = governor.ReservedBytes;
            Assert.True(pattern.TryMatchSuffix(bytes, shortest: true, CancellationToken.None, out start));
            Assert.Equal(bytes.Length - 2, start);
            Assert.Equal(reserved, governor.ReservedBytes);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void RefusalOrCancellationBeforeAtomAllocationRefundsTheOwner()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var governor = new MemoryGovernor(cancel ? 128 * 1024 : 8 * 1024);
            var admission = new CountingMemoryBudget(governor);
            var memory = new EvaluationMemory(admission);
            try
            {
                var pattern = ShellPattern.FromUnescaped(ShellValue.FromText("*" + new string('a', 4096) + "b"),
                    new GlobMask(ReadOnlyMemory<byte>.Empty), memory, cancellation.Token);
                if (cancel)
                {
                    admission.AdmissionSucceeded += cancellation.Cancel;
                    var failure = Assert.ThrowsAny<OperationCanceledException>(() => pattern.TryMatchSuffix("ab"u8, false, cancellation.Token, out _));
                    Assert.Equal(cancellation.Token, failure.CancellationToken);
                }
                else
                    Assert.Throws<MemoryBudgetExceededException>(() => pattern.TryMatchSuffix("ab"u8, false, cancellation.Token, out _));
            }
            finally { memory.Finish(retainVariables: false); }
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Fact]
    public void UnclosedBracketsKeepExplicitScalarAndByteAtomBoundariesInBothDirections()
    {
        var governor = new MemoryGovernor(128 * 1024);
        var memory = new EvaluationMemory(governor);
        var bytes = Encoding.UTF8.GetBytes("é" + new string('[', 512));
        var mask = default(GlobMaskSpan);
        try
        {
            foreach (var byteCharacters in new[] { false, true })
            {
                var expected = byteCharacters ? Enumerable.Range(0, bytes.Length)
                    : Enumerable.Range(2, 512).Prepend(0);
                var atoms = TextGlobMatcher.PrepareUtf8Atoms(bytes, in mask, byteCharacters, memory, CancellationToken.None);
                Assert.Equal(expected, atoms.ToArray());
                Assert.Equal(GlobMatchResult.Match,
                    TextGlobMatcher.MatchUtf8Suffix(bytes, in mask, atoms.Span, bytes,
                        false, byteCharacters, memory.Workspace, CancellationToken.None, out var length));
                Assert.Equal(bytes.Length, length);
                Assert.Equal(GlobMatchResult.Match,
                    TextGlobMatcher.FindUtf8(bytes, in mask, bytes, byteCharacters,
                        memory.Workspace, CancellationToken.None, out var start, out length));
                Assert.Equal(0, start);
                Assert.Equal(bytes.Length, length);
            }
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }
}
