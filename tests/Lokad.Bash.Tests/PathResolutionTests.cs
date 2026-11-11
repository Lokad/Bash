using Lokad.Bash.Helpers;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class PathResolutionTests
{
    [Theory]
    [InlineData(FilePathKind.Other)]
    [InlineData(FilePathKind.BlockDevice)]
    [InlineData(FilePathKind.CharacterDevice)]
    [InlineData(FilePathKind.Fifo)]
    [InlineData(FilePathKind.Socket)]
    public async Task ClassifyMissingPath_ReportsSpecialFileAncestorsAsNotDirectory(FilePathKind ancestorKind)
    {
        var host = new TestOperationVisitor([]);
        host.EnqueuePathInfo(FilePathInfo.FromKind(FilePathKind.Missing));
        host.EnqueuePathInfo(FilePathInfo.FromKind(ancestorKind));

        var kind = await PathResolution.ClassifyMissingPathAsync(new ShellPath("/special/missing/child"),
            static path => path.Parent,
            (path, token) => host.GetPathKindAsync(new FilePathLookup(path, FileLinkFollowing.Follow), token), CancellationToken.None);

        Assert.Equal(OperationErrorKind.NotDirectory, kind);
        Assert.Equal(new[] { "/special/missing", "/special" }, host.PathInfoRequests.Select(path => path.Path));
    }

    [Theory]
    [InlineData("./a//../b/", "/base", "/base/b")]
    [InlineData("/other/路径", "/base", "/other/路径")]
    [InlineData(@"  a\b  ", "/base", @"/base/  a\b  ")]
    public void TryResolveArgument_PreservesDisplayAndMatchesThrowingResolution(string raw, string current, string expected)
    {
        var directory = new ShellPath(current);
        Assert.True(PathResolution.TryResolveArgument(raw, directory, out var result));
        Assert.Equal(expected, result.Absolute.Path);
        Assert.Equal(raw, Utf8Text.Decode(result.Display));
        Assert.Equal(PathResolution.ResolveArgument(raw, directory).Absolute, result.Absolute);
    }

    [Theory]
    [InlineData("", typeof(FormatException))]
    [InlineData("a\0b", typeof(FormatException))]
    [InlineData("a\rb", typeof(FormatException))]
    [InlineData("a\nb", typeof(FormatException))]
    [InlineData("a\u007fb", typeof(FormatException))]
    [InlineData("../file", typeof(ShellPathException))]
    [InlineData("/a/../../file", typeof(ShellPathException))]
    public void TryResolveArgument_RejectsInvalidPathsWithoutThrowing(string raw, Type exceptionType)
    {
        Assert.False(PathResolution.TryResolveArgument(raw, ShellPath.Root, out _));
        var thrown = Assert.Throws(exceptionType, () => PathResolution.ResolveArgument(raw, ShellPath.Root));
        var original = Assert.Throws(exceptionType, () => RawShellPath.Parse(raw, null).ToAbsolute(ShellPath.Root));
        Assert.Equal(original.Message, thrown.Message);
    }

    [Theory]
    [InlineData("", "/", "/")]
    [InlineData(".", "/", "/")]
    [InlineData("a/b", "/", "/a/b")]
    [InlineData("a/./b", "/", "/a/b")]
    [InlineData("a/../b", "/", "/b")]
    [InlineData("/a//b///c", "/", "/a/b/c")]
    [InlineData("../x", "/a/b", "/a/x")]
    [InlineData("..", "/a", "/")]
    [InlineData("/already/absolute", "/ignored", "/already/absolute")]
    [InlineData("  a  ", "/", "/  a  ")]
    [InlineData(@"a\b", "/", @"/a\b")]
    public void ResolveAbsolute_NormalizesAsExpected(string path, string currentDirectory, string expected)
    {
        var actual = PathResolution.ResolveAbsolute(path, currentDirectory);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveAbsolute_UsesRootWhenCurrentDirectoryIsInvalid()
    {
        var actual = PathResolution.ResolveAbsolute("a", "relative");

        Assert.Equal("/a", actual);
    }

    [Fact]
    public void ResolveAbsolute_ReturnsRootWhenBothPathAndCurrentDirectoryAreInvalid()
    {
        var actual = PathResolution.ResolveAbsolute(string.Empty, "relative");

        Assert.Equal("/", actual);
    }
}
