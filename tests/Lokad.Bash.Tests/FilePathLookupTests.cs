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

    [Fact]
    public void RejectsUnsupportedFollowingPolicy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FilePathLookup(ShellPath.Root, (FileLinkFollowing)99));
    }
}
