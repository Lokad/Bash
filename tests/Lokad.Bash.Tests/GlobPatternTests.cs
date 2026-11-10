using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests;

public sealed class GlobPatternTests
{
    [Fact]
    public void TryFromExpandedWordRejectsNonUtf8()
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromBytes(new byte[] { 0xff }), false)]);

        Assert.False(GlobPattern.TryFromExpandedWord(word, new ExpansionBudget().Words, out var pattern));
        Assert.Null(pattern);
    }

    [Fact]
    public void PathComponentsAreChargedOnlyWhenRequested()
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("a/b"), isGlobQuoted: false)]);
        var budget = new ExpansionBudget().Words;
        budget.Charge(65536, 0);

        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));

        Assert.True(pattern.IsMatch("a/b"));
        Assert.Throws<ExpansionBudget.LimitExceededException>(() => pattern.SplitPathSegments(budget));
    }

    [Fact]
    public void QuoteMetadataRetainsTheCharacterAllowance()
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("a/b"), isGlobQuoted: false)]);
        var budget = new ExpansionBudget().Words;
        // Decoding fits, but quote metadata still reserves three more character units.
        budget.Charge(0, 16 * 1024 * 1024 - 5);

        Assert.Throws<ExpansionBudget.LimitExceededException>(() => GlobPattern.TryFromExpandedWord(word, budget, out _));
    }

    [Fact]
    public void EmptyQuotedSegmentsPreserveActivePathComponents()
    {
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromText("dir/"), isGlobQuoted: false),
            new ExpandedSegment(ShellValue.Empty, isGlobQuoted: true),
            new ExpandedSegment(ShellValue.FromText("?/[a]"), isGlobQuoted: false)]);
        var budget = new ExpansionBudget().Words;
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));

        Assert.True(pattern.IsMatch("dir/é/a"));
        var segments = pattern.SplitPathSegments(budget);
        Assert.Equal(["dir", "?", "[a]"], segments.Select(segment => segment.Pattern));
        Assert.Equal([false, true, true], segments.Select(segment =>
            GlobPattern.RequiresExpansion(segment.Pattern.AsSpan(), segment.MetaMask.AsSpan())));
    }

    [Fact]
    public void PathSubstringCopiesRetainTheCharacterAllowance()
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("a/b"), isGlobQuoted: false)]);
        var budget = new ExpansionBudget().Words;
        budget.Charge(0, 16 * 1024 * 1024 - 8);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));

        Assert.Throws<ExpansionBudget.LimitExceededException>(() => pattern.SplitPathSegments(budget));
    }

    [Theory]
    [InlineData("*", 200)]
    [InlineData("*/..", 64)]
    public async Task GlobPathCopiesConsumeTheCharacterAllowance(string pattern, int nameLength)
    {
        var budget = new ExpansionBudget().Words;
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(pattern), isGlobQuoted: false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        var glob = new GlobEvaluation(parsed, "/", budget, CancellationToken.None);
        // Leave room for a short name, but not a long name or the subsequent parent path copies.
        budget.Charge(0, 16 * 1024 * 1024 - 128);
        var listing = new GlobListing(() => [new DirectoryEntry(new string('a', nameLength), FilePathKind.Directory)]);

        await Assert.ThrowsAsync<ExpansionBudget.LimitExceededException>(() => glob.EvaluateAsync(listing));
    }

    [Fact]
    public async Task GlobExpansionObservesCancellationAfterListing()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = new ExpansionBudget().Words;
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("*"), isGlobQuoted: false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        var glob = new GlobEvaluation(parsed, "/", budget, cancellation.Token);
        var listing = new GlobListing(() =>
        {
            cancellation.Cancel();
            return [new DirectoryEntry("match", FilePathKind.File)];
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => glob.EvaluateAsync(listing));
    }

    private sealed class GlobListing(Func<IReadOnlyList<DirectoryEntry>> list) : IFileTester
    {
        public Task<IReadOnlyList<DirectoryEntry>> ListDirectoryAsync(string path) => Task.FromResult(list());

        public Task<bool> IsDirectoryAsync(string path) => throw new InvalidOperationException("No link targets expected.");
    }

    [Theory]
    [InlineData("[", false)]
    [InlineData("[abc", false)]
    [InlineData("]/[", false)]
    [InlineData("dir[/file]", false)]
    [InlineData("[a]", true)]
    [InlineData("[[]", true)]
    [InlineData("[*", true)]
    [InlineData("[?", true)]
    [InlineData("dir/*", true)]
    public void RequiresExpansion_RejectsUnclosedBracketComponents(string pattern, bool expected)
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(pattern), isGlobQuoted: false)]);

        Assert.Equal(expected, GlobPattern.RequiresExpansion(word));
        var budget = new ExpansionBudget().Words;
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        Assert.Equal(expected, parsed.SplitPathSegments(budget).Any(static segment =>
            GlobPattern.RequiresExpansion(segment.Pattern.AsSpan(), segment.MetaMask.AsSpan())));
    }

    [Theory]
    [InlineData("FF", false)]
    [InlineData("FF5B", false)]
    [InlineData("FF5B615D", true)]
    [InlineData("FF2A", true)]
    [InlineData("FF5B2F5D", false)]
    public void RequiresExpansion_InspectsRawBytes(string hex, bool expected)
    {
        var word = new ExpandedWord(
            [new ExpandedSegment(ShellValue.FromBytes(Convert.FromHexString(hex)), isGlobQuoted: false)]);

        Assert.Equal(expected, GlobPattern.RequiresExpansion(word));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("?")]
    [InlineData("[a]")]
    public void RequiresExpansion_PreservesQuotedMetacharacters(string pattern)
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(pattern), isGlobQuoted: true)]);

        Assert.False(GlobPattern.RequiresExpansion(word));
        var budget = new ExpansionBudget().Words;
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        Assert.DoesNotContain(parsed.SplitPathSegments(budget), static segment =>
            GlobPattern.RequiresExpansion(segment.Pattern.AsSpan(), segment.MetaMask.AsSpan()));
    }

    [Theory]
    [InlineData("[", "a", false, "]", true)]
    [InlineData("[", "a", true, "]", true)]
    [InlineData("[", "]", true, "", false)]
    [InlineData("[", "/", true, "]", false)]
    [InlineData("[", "/", false, "]", false)]
    [InlineData("[", "*", true, "", false)]
    public void RequiresExpansion_PreservesBoundariesAcrossSegments(
        string prefix, string middle, bool quoted, string suffix, bool expected)
    {
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromText(prefix), isGlobQuoted: false),
            new ExpandedSegment(ShellValue.FromText(middle), isGlobQuoted: quoted),
            new ExpandedSegment(ShellValue.FromText(suffix), isGlobQuoted: false)]);

        Assert.Equal(expected, GlobPattern.RequiresExpansion(word));
        var budget = new ExpansionBudget().Words;
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        Assert.Equal(expected, parsed.SplitPathSegments(budget).Any(static segment =>
            GlobPattern.RequiresExpansion(segment.Pattern.AsSpan(), segment.MetaMask.AsSpan())));
    }

    [Theory]
    [InlineData("[", "]", true)]
    [InlineData("[", "", false)]
    [InlineData("[", "/]", false)]
    [InlineData("", "*", true)]
    public void RequiresExpansion_InspectsOperatorsWithoutDecodingLiteralBytes(
        string prefix, string suffix, bool expected)
    {
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromText(prefix), isGlobQuoted: false),
            new ExpandedSegment(ShellValue.FromBytes(new byte[] { 0xff }), isGlobQuoted: true),
            new ExpandedSegment(ShellValue.FromText(suffix), isGlobQuoted: false)]);

        Assert.Equal(expected, GlobPattern.RequiresExpansion(word));
    }
}
