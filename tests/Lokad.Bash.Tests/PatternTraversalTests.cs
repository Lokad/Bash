using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class PatternTraversalTests
{
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
                                    requireEnd: true, byteCharacters: true, CancellationToken.None, out _) != GlobMatchResult.Match)
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
                        CancellationToken.None, out var actualStart, out var actualLength);
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
                            shortest, byteCharacters: true, CancellationToken.None, out var suffixLength);
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
}
