using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Tree;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class GlobPatternTests
{
    [Fact]
    public void TryFromExpandedWordRejectsNonUtf8()
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromBytes(new byte[] { 0xff }), false)]);

        Assert.False(GlobPattern.TryFromExpandedWord(word, new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes)), out var pattern));
        Assert.Null(pattern);
    }

    [Fact]
    public void EmptyQuotedSegmentsPreserveActivePathComponents()
    {
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromText("dir/"), isGlobQuoted: false),
            new ExpandedSegment(ShellValue.Empty, isGlobQuoted: true),
            new ExpandedSegment(ShellValue.FromText("?/[a]"), isGlobQuoted: false)]);
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));

        Assert.True(pattern.IsMatch("dir/é/a"));
        var components = PathComponents(pattern).ToArray();
        Assert.Equal(["dir", "?", "[a]"], components.Select(component => pattern.Pattern[component]));
        Assert.Equal([false, true, true], components.Select(component => IsActive(pattern, component)));
    }

    [Fact]
    public async Task GlobExpansionObservesCancellationAfterListing()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("*"), isGlobQuoted: false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        var glob = new GlobEvaluation(parsed, ShellPath.Root, budget, cancellation.Token);
        var listing = new GlobListing(_ =>
        {
            cancellation.Cancel();
            return [new DirectoryEntry("match", FilePathKind.File)];
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => glob.EvaluateAsync(listing));
    }

    [Fact]
    public async Task LongLiteralPrefixUsesOneLookupWithinLinearAllowance()
    {
        const int Count = 1024;
        var prefix = string.Concat(Enumerable.Repeat("a//", Count));
        var expectedLookup = string.Concat(Enumerable.Repeat("/a", Count));
        var governor = new MemoryGovernor(128 * 1024);
        using var scope = governor.CreateScope();
        var budget = new EvaluationMemory(scope);
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(prefix + "*"), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));
        var glob = new GlobEvaluation(pattern, ShellPath.Root, budget, CancellationToken.None);
        var listed = new List<string>();
        var listing = new GlobListing(path =>
        {
            listed.Add(path);
            return [new DirectoryEntry("hit", FilePathKind.File)];
        });

        Assert.Equal(prefix + "hit", Assert.Single(await glob.EvaluateAsync(listing)));
        Assert.Equal(expectedLookup, Assert.Single(listed));
        budget.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task LiteralPrefixAdmissionRefusalPrecedesListing()
    {
        var governor = new MemoryGovernor(32 * 1024);
        using var scope = governor.CreateScope();
        var budget = new EvaluationMemory(scope);
        var prefix = string.Concat(Enumerable.Repeat("a/", 128));
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(prefix + "*"), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));
        var glob = new GlobEvaluation(pattern, ShellPath.Root, budget, CancellationToken.None);
        budget.ReleaseUnusedCapacity();
        var listing = new GlobListing(_ => throw new InvalidOperationException("No listing expected."));
        using (governor.Reserve(governor.LimitBytes - governor.ReservedBytes))
            await Assert.ThrowsAsync<MemoryBudgetExceededException>(() => glob.EvaluateAsync(listing));

        budget.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class GlobListing(Func<string, IReadOnlyList<DirectoryEntry>> list) : IFileTester
    {
        public Task<IReadOnlyList<DirectoryEntry>> ListDirectoryAsync(string path) => Task.FromResult(list(path));

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
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        Assert.Equal(expected, PathComponents(parsed).Any(component => IsActive(parsed, component)));
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
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        Assert.DoesNotContain(PathComponents(parsed), component => IsActive(parsed, component));
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
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        Assert.Equal(expected, PathComponents(parsed).Any(component => IsActive(parsed, component)));
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

    private static IEnumerable<Range> PathComponents(GlobPattern pattern)
    {
        var cursor = 0;
        while (pattern.TryReadPathComponent(ref cursor, out var component))
            yield return component;
    }

    private static bool IsActive(GlobPattern pattern, Range component) =>
        GlobPattern.RequiresExpansion(pattern.Pattern.AsSpan(component), pattern.GetMask(component));
}
