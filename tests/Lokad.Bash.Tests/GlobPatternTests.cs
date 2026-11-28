using System.Collections;
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

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, false)]
    public void PathnameBracketsRespectQuotedSegmentsAndComponentBoundaries(
        bool openingQuoted, bool separatesComponents, bool closingQuoted, bool expands)
    {
        var ordinary = new string('x', 4096);
        var gap = separatesComponents ? ordinary + "/" + ordinary : ordinary;
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromText(ordinary + "["), openingQuoted),
            new ExpandedSegment(ShellValue.FromText(gap), isGlobQuoted: true),
            new ExpandedSegment(ShellValue.FromText("]" + ordinary), closingQuoted)]);
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);

        Assert.Equal(expands, GlobPattern.RequiresExpansion(word));
        Assert.True(GlobPattern.TryFromExpandedWord(word, memory, out var pattern));
        var mask = pattern.GetMask(0..pattern.Pattern.Length);
        Assert.Equal(expands, GlobPattern.RequiresExpansion(pattern.Pattern.AsSpan(), in mask));
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("p", false)]
    [InlineData("p", true)]
    [InlineData("🙂", false)]
    [InlineData("🙂", true)]
    public void PathnameMaskAllowanceFollowsQuotedContent(string scalar, bool quoted)
    {
        var prefix = string.Concat(Enumerable.Repeat(scalar, 512));
        var literal = ShellValue.FromText(prefix + "[x]");
        var word = new ExpandedWord([
            new ExpandedSegment(literal, isGlobQuoted: quoted),
            new ExpandedSegment(ShellValue.Empty, isGlobQuoted: true),
            new ExpandedSegment(ShellValue.FromText("*"), isGlobQuoted: false)]);
        var textAllowance = 8L * (literal.Bytes.Length + 1);
        var maskAllowance = quoted ? 4L * (prefix.Length + "[x]*".Length) : 0;
        var governor = new MemoryGovernor(quoted ? 2 * textAllowance : textAllowance);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);

        Assert.True(GlobPattern.RequiresExpansion(word));
        Assert.True(GlobPattern.TryFromExpandedWord(word, memory, out var pattern));
        Assert.Equal(prefix + "[x]*", pattern.Pattern);
        Assert.Equal(quoted, pattern.IsMatch(prefix + "[x]tail"));
        Assert.Equal(!quoted, pattern.IsMatch(prefix + "xtail"));
        Assert.Equal(textAllowance + maskAllowance, memory.Workspace.Bytes);

        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task GlobExpansionObservesCancellationAfterListing()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("*"), isGlobQuoted: false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var parsed));
        using var globMemory = new MemoryCapacity(budget.Budget, 0);
        var glob = new GlobEvaluation(parsed, ShellPath.Root, globMemory, cancellation.Token);
        var listing = new GlobListing(_ =>
        {
            cancellation.Cancel();
            return [new DirectoryEntry("match", FilePathKind.File)];
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => glob.EvaluateAsync(listing));
    }

    [Fact]
    public async Task ExplicitDotGlobDoesNotDuplicateHostSuppliedDotEntries()
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        using var matches = new MemoryCapacity(owner, 0);
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(".*"), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, memory, out var pattern));
        var listing = new GlobListing(_ =>
            [new(".", FilePathKind.Directory), new("..", FilePathKind.Directory), new(".hidden", FilePathKind.File)]);
        var glob = new GlobEvaluation(pattern, ShellPath.Root, matches, CancellationToken.None, ShellOption.None);

        Assert.Equal([".", "..", ".hidden"], await glob.EvaluateAsync(listing));

        matches.Dispose();
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void CaseFoldedCachedBracketsPreserveCaseSensitiveNamedClasses()
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var memory = new MemoryCapacity(governor, 0);
        using (var matcher = new TextGlobMatcher.PathComponentMatcher(
                   "[[:upper:]c-n3456789012]".AsMemory(), default, memory, ignoreAsciiCase: true))
        {
            Assert.True(matcher.Matches("A", CancellationToken.None));
            Assert.False(matcher.Matches("a", CancellationToken.None));
            Assert.True(memory.Bytes > 0);
            Assert.True(matcher.Matches("Z", CancellationToken.None));
            Assert.False(matcher.Matches("z", CancellationToken.None));
            Assert.True(matcher.Matches("n", CancellationToken.None));
        }
        Assert.Equal(0, memory.Bytes);
        memory.ReleaseUnusedCapacity();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void PathComponentMatcherReusesWideMembershipAndRefundsIt()
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var memory = new MemoryCapacity(governor, 0);
        using (var matcher = new TextGlobMatcher.PathComponentMatcher(
                   ("[!" + new string('a', 64) + "[:digit:]🙂]").AsMemory(), default, memory, ignoreAsciiCase: false))
        {
            Assert.False(matcher.Matches("🙂", CancellationToken.None));
            Assert.Equal(0, memory.Bytes);
            Assert.True(matcher.Matches("é", CancellationToken.None));
            var prepared = memory.Bytes;
            Assert.True(prepared > 0);
            Assert.False(matcher.Matches("5", CancellationToken.None));
            Assert.False(matcher.Matches("a", CancellationToken.None));
            Assert.False(matcher.Matches("éé", CancellationToken.None));
            Assert.Equal(prepared, memory.Bytes);
        }
        Assert.Equal(0, memory.Bytes);
        memory.ReleaseUnusedCapacity();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PathnameBracketStateSettlesThroughALinkLookup(bool failLookup)
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var spelling = "[" + new string('a', 64) + "]*/[" + new string('b', 64) + "]";
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(spelling), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, memory, out var pattern));
        using var matches = new MemoryCapacity(owner, 0);
        var listing = new PendingGlobLink();
        var operation = new GlobEvaluation(pattern, ShellPath.Root, matches, CancellationToken.None).EvaluateAsync(listing);

        Assert.False(operation.IsCompleted);
        Assert.Equal(["/", "/a-link"], listing.Requests);
        var pendingBytes = matches.Bytes;
        if (failLookup)
        {
            listing.Link.SetException(new InvalidOperationException("synthetic link lookup failure"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.True(matches.Bytes < pendingBytes);
            Assert.Equal(["/", "/a-link"], listing.Requests);
        }
        else
        {
            listing.Link.SetResult(true);
            Assert.Equal(["a-link/b", "a-real/b"], await operation);
            Assert.Equal(["/", "/a-link", "/a-real", "/a-link"], listing.Requests);
        }
        matches.Dispose();
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class PendingGlobLink : IFileTester
    {
        public List<string> Requests { get; } = [];
        public TaskCompletionSource<bool> Link { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DirectoryListingResult> ListDirectoryAsync(string path)
        {
            Requests.Add(path);
            IReadOnlyList<DirectoryEntry> entries = path == "/"
                ? [new("a-real", FilePathKind.Directory), new("a-link", FilePathKind.SymbolicLink),
                    new("a-file", FilePathKind.File), new(".a-hidden", FilePathKind.Directory)]
                : [new("a", FilePathKind.File), new("b", FilePathKind.File)];
            return Task.FromResult(DirectoryListingResult.Success(entries));
        }

        public Task<bool> IsDirectoryAsync(string path)
        {
            Requests.Add(path);
            return Link.Task;
        }
    }

    [Theory]
    [InlineData("**/leaf", "a/leaf|a/sub/leaf|leaf")]
    [InlineData("a/**/leaf", "a/leaf|a/link/leaf|a/sub/back/leaf|a/sub/leaf")]
    [InlineData("**//leaf", "a/leaf|a/link/leaf|a/sub/back/leaf|a/sub/leaf|link/leaf")]
    [InlineData("**/", "a/|a/link/|a/sub/|a/sub/back/|link/")]
    [InlineData("**/link/leaf", "a/link/leaf|link/leaf")]
    [InlineData("link/**/leaf", "link/leaf|link/link/leaf|link/sub/back/leaf|link/sub/leaf")]
    public async Task GlobstarListsLinkTargetsOnlyForReachedSuffixes(string spelling, string expected)
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var scope = governor.CreateScope();
        var memory = new EvaluationMemory(scope);
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(spelling), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, memory, out var pattern));
        using (var globMemory = new MemoryCapacity(scope, 0))
        {
            var glob = new GlobEvaluation(pattern, ShellPath.Root, globMemory, CancellationToken.None,
                ShellOptions.Default | ShellOption.Globstar);
            Assert.Equal(expected.Split('|'), await glob.EvaluateAsync(new RecursiveLinkListing()));
        }
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class RecursiveLinkListing : IFileTester
    {
        private static readonly IReadOnlyList<DirectoryEntry> Root =
            [new("a", FilePathKind.Directory), new("leaf", FilePathKind.File), new("link", FilePathKind.SymbolicLink)];

        private static readonly IReadOnlyList<DirectoryEntry> Branch =
            [new("sub", FilePathKind.Directory), new("leaf", FilePathKind.File), new("link", FilePathKind.SymbolicLink)];

        private static readonly IReadOnlyList<DirectoryEntry> Subdirectory =
            [new("leaf", FilePathKind.File), new("back", FilePathKind.SymbolicLink)];

        public Task<DirectoryListingResult> ListDirectoryAsync(string path) =>
            Task.FromResult(DirectoryListingResult.Success(path switch
            {
                "/" => Root,
                "/a" or "/link" or "/a/sub/back" or "/link/sub/back" => Branch,
                "/a/sub" or "/a/link" or "/link/sub" or "/link/link" => Subdirectory,
                _ => throw new InvalidOperationException("Unexpected recursive listing: " + path)
            }));

        public Task<bool> IsDirectoryAsync(string path) =>
            Task.FromResult(path is "/link" or "/a/link" or "/a/sub/back");
    }

    [Fact]
    public async Task GlobstarChargesPendingPathsBeforeListingThem()
    {
        var governor = new MemoryGovernor(16 * 1024);
        using var scope = governor.CreateScope();
        var memory = new EvaluationMemory(scope);
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("**/leaf"), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, memory, out var pattern));
        var entries = Enumerable.Range(0, 4096)
            .Select(index => new DirectoryEntry($"branch_{index:D4}", FilePathKind.Directory)).ToArray();
        var listing = new GlobListing(path => path == "/" ? entries
            : throw new InvalidOperationException("A pending path was listed before admission."));
        using (var globMemory = new MemoryCapacity(scope, 0))
        {
            var glob = new GlobEvaluation(pattern, ShellPath.Root, globMemory, CancellationToken.None,
                ShellOptions.Default | ShellOption.Globstar);
            await Assert.ThrowsAsync<MemoryBudgetExceededException>(() => glob.EvaluateAsync(listing));
        }
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
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
        using var globMemory = new MemoryCapacity(scope, 0);
        var glob = new GlobEvaluation(pattern, ShellPath.Root, globMemory, CancellationToken.None);
        var listed = new List<string>();
        var listing = new GlobListing(path =>
        {
            listed.Add(path);
            return [new DirectoryEntry("hit", FilePathKind.File)];
        });

        Assert.Equal(prefix + "hit", Assert.Single(await glob.EvaluateAsync(listing)));
        Assert.Equal(expectedLookup, Assert.Single(listed));
        globMemory.Dispose();
        budget.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("😀//./", "/😀/.")]
    [InlineData("//😀///../", "/😀/..")]
    public async Task GlobSortOrdersFullPathsByUtf8WithALiteralPrefix(string prefix, string lookup)
    {
        string[] names = ["a", "aa", "\uE000", "\U00010000", "\U00010001"];
        var entries = names.Reverse().Select(name => new DirectoryEntry(name, FilePathKind.File)).ToArray();
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(prefix + "*"), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, memory, out var pattern));
        using var resultMemory = new MemoryCapacity(owner, 0);
        var listing = new GlobListing(path =>
        {
            Assert.Equal(lookup, path);
            return entries;
        });

        var result = await new GlobEvaluation(pattern, ShellPath.Root, resultMemory, CancellationToken.None).EvaluateAsync(listing);

        Assert.Equal(names.Select(name => prefix + name), result);
        resultMemory.Dispose();
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("*", FilePathKind.File, "")]
    [InlineData("*/", FilePathKind.Directory, "/")]
    public async Task TerminalGlobKeepsOnlyItsOutputSpelling(string spelling, FilePathKind kind, string suffix)
    {
        var directory = new ShellPath("/" + string.Join('/', Enumerable.Repeat(new string('d', 64), 12)));
        var governor = new MemoryGovernor(96 * 1024);
        using var scope = governor.CreateScope();
        var budget = new EvaluationMemory(scope);
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(spelling), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));
        using var globMemory = new MemoryCapacity(scope, 0);
        var glob = new GlobEvaluation(pattern, directory, globMemory, CancellationToken.None);
        var entries = Enumerable.Range(0, 256)
            .Select(index => new DirectoryEntry($"entry_{index:D3}", kind)).ToArray();
        var listings = 0;
        var listing = new GlobListing(path =>
        {
            Assert.Equal(directory.Path, path);
            listings++;
            return entries.Reverse().ToArray();
        });

        Assert.Equal(entries.Select(entry => entry.Name + suffix), await glob.EvaluateAsync(listing));
        Assert.Equal(1, listings);
        globMemory.Dispose();
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
        using var globMemory = new MemoryCapacity(scope, 0);
        var glob = new GlobEvaluation(pattern, ShellPath.Root, globMemory, CancellationToken.None);
        budget.ReleaseUnusedCapacity();
        var listing = new GlobListing(_ => throw new InvalidOperationException("No listing expected."));
        using (governor.Reserve(governor.LimitBytes - governor.ReservedBytes))
            await Assert.ThrowsAsync<MemoryBudgetExceededException>(() => glob.EvaluateAsync(listing));

        globMemory.Dispose();
        budget.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobIgnoreAdmitsOnlySurvivingTerminalPaths(bool keepMatches)
    {
        var governor = new MemoryGovernor(32 * 1024);
        using var scope = governor.CreateScope();
        var memory = new EvaluationMemory(scope);
        var prefix = new string('p', 256) + "//";
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText(prefix + "*"), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, memory, out var pattern));
        var entries = Enumerable.Range(0, 4096)
            .Select(index => new DirectoryEntry($"discard_{index:D4}", FilePathKind.File)).ToList();
        string[] survivors = ["z.keep", "a.keep"];
        if (keepMatches)
            entries.AddRange(survivors.Select(name => new DirectoryEntry(name, FilePathKind.File)));

        var listing = new GlobListing(_ => entries);
        using (var resultMemory = new MemoryCapacity(scope, 0))
        using (var ignore = new GlobIgnorePatterns(ShellValue.FromText(prefix + "discard_*"), false, memory))
        {
            var glob = new GlobEvaluation(pattern, ShellPath.Root, resultMemory, CancellationToken.None);
            var result = await glob.EvaluateAsync(listing, ignore);
            if (keepMatches)
                Assert.Equal(survivors.Reverse().Select(name => prefix + name), result);
            else
                Assert.Empty(result);
        }
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void GlobIgnoreCachedBracketsRespectMemberOrderAtSeparators()
    {
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using var scope = governor.CreateScope();
        var memory = new EvaluationMemory(scope);
        using (var ignore = new GlobIgnorePatterns(ShellValue.FromText("a[xxxxxxxxxxxxxxxxxy/]*"), false, memory))
        {
            Assert.True(ignore.Excludes("ax/leaf", CancellationToken.None));
            Assert.False(ignore.Excludes("ay/leaf", CancellationToken.None));
            Assert.False(ignore.Excludes("a/leaf", CancellationToken.None));
            Assert.True(ignore.Excludes("ax/other", CancellationToken.None));
        }
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobIgnoreSettlesDeepListGroups(bool fits)
    {
        const int Depth = 4096;
        var governor = new MemoryGovernor(fits ? 64 * 1024 : 1024);
        using var scope = governor.CreateScope();
        var memory = new EvaluationMemory(scope);
        var spelling = new string('[', Depth) + "keep:middle" + new string(']', Depth) + ":drop";
        using (var filter = new GlobIgnorePatterns(ShellValue.FromText(spelling), false, memory))
        {
            if (fits)
            {
                Assert.True(filter.Excludes("drop", CancellationToken.None));
                Assert.True(governor.PeakBytes - governor.ReservedBytes >= Depth);
            }
            else
            {
                Assert.Throws<MemoryBudgetExceededException>(() => filter.Excludes("drop", CancellationToken.None));
                Assert.True(governor.ReservedBytes < 512);
            }
        }
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void GlobIgnoreReusesBoundariesOnEqualByteAssignments()
    {
        const string Spelling = "\"left:$(case a in a) printf \"left:middle:right\";; esac):right\":drop";
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using var scope = governor.CreateScope();
        var memory = new EvaluationMemory(scope);
        var state = new EvaluationState(Syntax.Parser.Parse(":"), ShellPath.Root, inheritedDescriptorTable: null);
        state.SetShellMemory(memory);

        state.RefreshGlobIgnore(ShellValue.FromText(Spelling));
        var ranges = Assert.IsAssignableFrom<IReadOnlyList<Range>>(state.GlobIgnoreRanges);
        var separator = Spelling.LastIndexOf(':');
        Assert.Equal(new[] { 0..separator, (separator + 1)..Spelling.Length }, ranges);
        state.RefreshGlobIgnore(ShellValue.FromText(Spelling));
        Assert.Same(ranges, state.GlobIgnoreRanges);

        state.RefreshGlobIgnore(ShellValue.FromText("drop"));
        Assert.Null(state.GlobIgnoreRanges);
        memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobSortAdmitsItsResultArrayAfterAllCandidates(bool canAdmitResult)
    {
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var scope = governor.CreateScope();
        var budget = new EvaluationMemory(scope);
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("*"), false)]);
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));
        using var globMemory = new MemoryCapacity(scope, 0);
        var glob = new GlobEvaluation(pattern, ShellPath.Root, globMemory, CancellationToken.None);
        var entries = Enumerable.Range(0, 4096)
            .Reverse().Select(index => new DirectoryEntry($"entry_{index:D4}", FilePathKind.File)).ToArray();
        using var held = governor.Reserve(0);
        var observed = new CompletedListing(entries, () =>
        {
            globMemory.ReleaseUnusedCapacity();
            // Leave room for the result and one prepaid growth increment in the success case.
            var allowance = canAdmitResult ? 96 * 1024 : 4096;
            held.Resize(governor.LimitBytes - governor.ReservedBytes - allowance);
        });

        var listing = new GlobListing(_ => observed);
        if (canAdmitResult)
        {
            var paths = await glob.EvaluateAsync(listing);
            Assert.Equal(entries.Reverse().Select(entry => entry.Name), paths);
        }
        else
            await Assert.ThrowsAsync<MemoryBudgetExceededException>(() => glob.EvaluateAsync(listing));
        Assert.True(held.Bytes > 0);
        globMemory.Dispose();
        budget.Finish(retainVariables: false);
        held.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class CompletedListing(DirectoryEntry[] entries, Action onCompleted) : IReadOnlyList<DirectoryEntry>
    {
        public int Count => entries.Length;
        public DirectoryEntry this[int index] => entries[index];

        public IEnumerator<DirectoryEntry> GetEnumerator()
        {
            foreach (var entry in entries)
                yield return entry;
            onCompleted();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GlobListing(Func<string, IReadOnlyList<DirectoryEntry>> list) : IFileTester
    {
        public Task<DirectoryListingResult> ListDirectoryAsync(string path) => Task.FromResult(DirectoryListingResult.Success(list(path)));

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
