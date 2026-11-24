using Lokad.Bash.Files;
namespace Lokad.Bash.Tests;

public sealed class FilePathLookupTests
{
    [Theory]
    [InlineData("link/../file", "/work", "/work/link/../file")]
    [InlineData("link/", "/", "/link/")]
    [InlineData("link/.", "/work", "/work/link/.")]
    [InlineData("/link/../file", "/work", "/link/../file")]
    [InlineData("//link//", "/work", "//link//")]
    public void AnchorsRelativePathsWithoutChangingTraversal(string operand, string directory, string expected)
    {
        var entry = new FileEntryPath(RawShellPath.Parse(operand), new ShellPath(directory));
        var lookup = new FilePathLookup(RawShellPath.Parse(operand), new ShellPath(directory), FileLinkFollowing.NoFollow);
        Assert.Equal(expected, entry.Path);
        Assert.Equal(entry, lookup.EntryPath);
        Assert.Equal(lookup, new FilePathLookup(entry, FileLinkFollowing.NoFollow));
        Assert.Equal(expected, lookup.Path);
        Assert.Equal(FileLinkFollowing.NoFollow, lookup.LinkFollowing);
    }

    [Fact]
    public void DefaultEntryPathRepresentsRoot()
    {
        var root = new FileEntryPath(ShellPath.Root);
        Assert.Equal("/", default(FileEntryPath).Path);
        Assert.Equal(root, default(FileEntryPath));
        Assert.Equal(root.GetHashCode(), default(FileEntryPath).GetHashCode());
        Assert.Equal(new FilePathLookup(ShellPath.Root, FileLinkFollowing.NoFollow),
            new FilePathLookup(default(FileEntryPath), FileLinkFollowing.NoFollow));
    }

    [Fact]
    public void EqualityIncludesPreservedSyntaxAndFollowingPolicy()
    {
        var follow = new FilePathLookup(new ShellPath("/link"), FileLinkFollowing.Follow);
        var raw = new FilePathLookup(RawShellPath.Parse("link"), ShellPath.Root, FileLinkFollowing.Follow);
        Assert.Equal(follow, raw);
        Assert.Equal(follow.GetHashCode(), raw.GetHashCode());
        Assert.NotEqual(follow, new FilePathLookup(new ShellPath("/link"), FileLinkFollowing.NoFollow));
        Assert.NotEqual(follow, new FilePathLookup(RawShellPath.Parse("link/"), ShellPath.Root, FileLinkFollowing.Follow));
        Assert.NotEqual(follow, new FilePathLookup(RawShellPath.Parse("./link"), ShellPath.Root, FileLinkFollowing.Follow));
    }

    [Theory]
    [InlineData("a/./b//./leaf", "/a/b/leaf")]
    [InlineData("a/.", "/a", "/a")]
    [InlineData("a/./", "/a", "/a")]
    [InlineData("../../leaf", "/leaf")]
    [InlineData("a/x/./../b/y/../leaf", "/a/b/leaf", "/a/x", "/a/b/y")]
    [InlineData("a/x/../../leaf", "/leaf", "/a/x", "/a")]
    [InlineData("a/b/../b/leaf", "/a/b/leaf")]
    [InlineData("a/b/../b", "/a/b", "/a/b")]
    [InlineData("a/b/.././b/leaf", "/a/b/leaf", "/a/b")]
    [InlineData("a/b/.././b/..", "/a", "/a/b", "/a/b")]
    [InlineData("a/b/.././b/", "/a/b", "/a/b", "/a/b")]
    public void LexicalResolutionRetainsRequiredDirectoriesInTraversalOrder(
        string operand, string expectedPath, params string[] expectedDirectories)
    {
        var entry = new FileEntryPath(RawShellPath.Parse(operand), ShellPath.Root);
        var (path, directories) = entry.ResolveWithoutSymbolicLinks();

        Assert.Equal(expectedPath, path.Path);
        Assert.Equal(expectedDirectories, directories.Select(directory => directory.Path));
    }

    [Theory]
    [InlineData("a/./b/", "/a/b")]
    [InlineData("a/b/./", "/a/b", "/a/b")]
    [InlineData("a/x/../b//", "/a/b", "/a/x")]
    public void DirectoryCreationAllowsTheFinalSlashWithoutLosingOtherChecks(
        string operand, string expectedPath, params string[] expectedDirectories)
    {
        var entry = new FileEntryPath(RawShellPath.Parse(operand), ShellPath.Root);
        var (path, directories) = entry.ResolveDirectoryCreationWithoutSymbolicLinks();

        Assert.Equal(expectedPath, path.Path);
        Assert.Equal(expectedDirectories, directories.Select(directory => directory.Path));
    }

    [Fact]
    public void CoveredDotCheckpointsDoNotCopyEveryAncestorPrefix()
    {
        const int components = 128;
        const int iterations = 32;
        const int maximumBytesPerResolution = 32 * 1024;
        var prefix = string.Concat(Enumerable.Repeat("/unit", components));
        var expectedPath = prefix + "/leaf";
        var grouped = prefix + string.Concat(Enumerable.Repeat("/.", components)) + "/leaf";
        var distributed = string.Concat(Enumerable.Repeat("/unit/.", components)) + "/leaf";
        Assert.Equal(grouped.Length, distributed.Length);

        foreach (var raw in new[] { grouped, distributed })
        {
            var entry = new FileEntryPath(RawShellPath.Parse(raw), ShellPath.Root);
            var (path, directories) = entry.ResolveWithoutSymbolicLinks();
            Assert.Equal(expectedPath, path.Path);
            Assert.Empty(directories);
            AllocationAssert.SustainedReuse(() => entry.ResolveWithoutSymbolicLinks(), iterations,
                iterations * maximumBytesPerResolution);
        }
    }

    [Fact]
    public void RejectsUnsupportedFollowingPolicy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FilePathLookup(ShellPath.Root, (FileLinkFollowing)99));
    }
}
