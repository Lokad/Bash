namespace Lokad.Bash.Tests;

public sealed class RawShellPathTests
{
    #region IsValid

    [Theory]
    [InlineData("/")]
    [InlineData("/foo")]
    [InlineData("/foo/bar")]
    [InlineData("foo")]
    [InlineData("foo/bar")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../foo")]
    [InlineData("./foo")]
    [InlineData("foo/./bar")]
    [InlineData("foo/../bar")]
    [InlineData("/foo/../bar")]
    [InlineData("/foo/./bar")]
    [InlineData("/foo/with space")]
    [InlineData("relative path")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a\\b")]
    [InlineData("C:\\xyz")]
    [InlineData("\\\\xyz")]
    [InlineData("/unicode/路径")]
    public void IsValid_AcceptsValidPaths(string s)
    {
        Assert.True(RawShellPath.IsValid(s));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has\ttab")]
    [InlineData("has\nnewline")]
    [InlineData("has\u007fdel")]
    public void IsValid_RejectsInvalidPaths(string? s)
    {
        Assert.False(RawShellPath.IsValid(s));
    }

    #endregion

    #region IParsable

    [Fact]
    public void TryParse_ReturnsFalseForInvalid()
    {
        Assert.False(RawShellPath.TryParse("", null, out _));
        Assert.False(RawShellPath.TryParse(null, null, out _));
        Assert.False(RawShellPath.TryParse("has\ttab", null, out _));
    }

    [Fact]
    public void TryParse_ReturnsTrueAndPreservesRaw()
    {
        Assert.True(RawShellPath.TryParse("../foo", null, out var result));
        Assert.Equal("../foo", result.Raw);
        Assert.Equal(result.Raw, RawShellPath.Parse("../foo").Raw);
    }

    [Fact]
    public void Parse_ThrowsFormatExceptionForInvalid()
    {
        var expected = Assert.Throws<FormatException>(() => RawShellPath.Parse("", null));
        var actual = Assert.Throws<FormatException>(() => RawShellPath.Parse(""));
        Assert.Equal(expected.Message, actual.Message);
    }

    #endregion

    #region ToAbsolute — basic resolution

    [Theory]
    [InlineData("/foo/bar", "/base", "/foo/bar")]       // absolute raw ignores current
    [InlineData("baz",     "/base", "/base/baz")]       // relative appended to current
    [InlineData("baz",     "/",     "/baz")]            // current is root
    [InlineData(".",       "/base", "/base")]            // dot is current
    [InlineData("..",      "/base", "/")]                // parent of /base is root
    [InlineData("../sibling", "/base", "/sibling")]     // relative up then down
    [InlineData("/",       "/base", "/")]               // absolute root
    [InlineData("foo/./bar", "/base", "/base/foo/bar")] // dot in middle
    [InlineData("foo/../bar", "/base", "/base/bar")]    // dotdot collapses
    [InlineData("//foo///bar/", "/base", "/foo/bar")]
    [InlineData("with space/路径", "/base", "/base/with space/路径")]
    [InlineData(@"a\b", "/base", @"/base/a\b")]
    public void ToAbsolute_ResolvesCorrectly(string raw, string current, string expected)
    {
        var path = RawShellPath.Parse(raw, null);
        var directory = new ShellPath(current);
        var result = path.ToAbsolute(directory);
        Assert.Equal(expected, result.Path);
        Assert.True(path.TryToAbsolute(directory, jail: false, out var tried));
        Assert.Equal(result, tried);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToAbsolute_AboveRoot_Throws(bool jail)
    {
        var raw = RawShellPath.Parse("..", null);
        var ex = Assert.Throws<ShellPathException>(() => raw.ToAbsolute(ShellPath.Root, jail));
        Assert.Contains("root", ex.Message);
        Assert.False(raw.TryToAbsolute(ShellPath.Root, jail, out var result));
        Assert.Null(result);
    }

    #endregion

    [Fact]
    public void ToAbsolute_RequiredDirectoriesIncludesCanonicalParent()
    {
        var requiredDirectories = new HashSet<ShellPath>();

        var result = RawShellPath.Parse("a/b", null).ToAbsolute(
            new ShellPath("/work"),
            jail: false,
            requiredDirectories: requiredDirectories);

        Assert.Equal("/work/a/b", result.Path);
        Assert.Equal(new ShellPath("/work/a"), Assert.Single(requiredDirectories));
    }

    [Fact]
    public void ToAbsolute_RequiredDirectoriesIncludesTrailingSlashTarget()
    {
        var requiredDirectories = new HashSet<ShellPath>();

        var result = RawShellPath.Parse("a/b/", null).ToAbsolute(
            new ShellPath("/work"),
            jail: false,
            requiredDirectories: requiredDirectories);

        Assert.Equal("/work/a/b", result.Path);
        Assert.Equal(new ShellPath("/work/a/b"), Assert.Single(requiredDirectories));
    }

    [Fact]
    public void ToAbsolute_RequiredDirectoriesRetainsIncomparableCheckpoints()
    {
        var raw = RawShellPath.Parse("a/b/../c/../leaf", null);
        var requiredDirectories = new HashSet<ShellPath>();

        raw.ToAbsolute(
            ShellPath.Root,
            jail: false,
            requiredDirectories: requiredDirectories);

        Assert.Contains(new ShellPath("/a/b"), requiredDirectories);
        Assert.Contains(new ShellPath("/a/c"), requiredDirectories);
    }


    [Theory]
    [InlineData("a/b", "/work", "/work/a/b", "/work/a")]
    [InlineData("a/b/", "/work", "/work/a/b", "/work/a/b")]
    [InlineData("a//b", "/work", "/work/a/b", "/work/a")]
    [InlineData("a/./b", "/work", "/work/a/b", "/work/a")]
    [InlineData("a/../b", "/work", "/work/b", "/work/a", "/work")]
    [InlineData("a/b/../c/../leaf", "/", "/a/leaf", "/a/b", "/a/c", "/a")]
    [InlineData("a///./b//../c/./../leaf", "/", "/a/leaf", "/a", "/a/b", "/a/c")]
    [InlineData("/", "/work", "/", "/")]
    public void ToAbsolute_CheckpointsPreserveNormalization(
        string raw,
        string current,
        string expected,
        params string[] expectedDirectories)
    {
        var path = RawShellPath.Parse(raw, null);
        var directory = new ShellPath(current);
        var requiredDirectories = new HashSet<ShellPath>();

        var result = path.ToAbsolute(directory, jail: false, requiredDirectories);

        Assert.Equal(expected, result.Path);
        Assert.Equal(path.ToAbsolute(directory, jail: false), result);
        Assert.True(requiredDirectories.SetEquals(expectedDirectories.Select(p => new ShellPath(p))));
        requiredDirectories.Clear();
        Assert.True(path.TryToAbsolute(directory, jail: false, requiredDirectories, out var tried));
        Assert.Equal(result, tried);
        Assert.True(requiredDirectories.SetEquals(expectedDirectories.Select(p => new ShellPath(p))));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/.")]
    public void TryToAbsolute_RepeatedUnchangedCheckpointsHaveBoundedAllocations(string suffix)
    {
        var directory = "/" + new string('a', 2047);
        var raw = RawShellPath.Parse(directory + string.Concat(Enumerable.Repeat(suffix, 2048 / suffix.Length)));
        var requiredDirectories = new HashSet<ShellPath>();
        raw.TryToAbsolute(ShellPath.Root, jail: false, requiredDirectories, out _);
        requiredDirectories.Clear();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var success = raw.TryToAbsolute(ShellPath.Root, jail: false, requiredDirectories, out var result);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(success);
        Assert.Equal(directory, result?.Path);
        Assert.Contains(new ShellPath(directory), requiredDirectories);
        // Leave ample runtime headroom while rejecting the former multi-megabyte copies.
        Assert.InRange(allocated, 0, 128 * 1024);
    }

    [Theory]
    [InlineData("..", "/", false)]
    [InlineData("..", "/", true)]
    [InlineData("..", "/jail", true)]
    [InlineData("/jailbird", "/jail", true)]
    public void ToAbsolute_CheckpointsPreserveFailures(string raw, string current, bool jail)
    {
        var path = RawShellPath.Parse(raw, null);
        var directory = new ShellPath(current);
        var requiredDirectories = new HashSet<ShellPath>();
        var expected = Assert.Throws<ShellPathException>(() => path.ToAbsolute(directory, jail));

        var actual = Assert.Throws<ShellPathException>(
            () => path.ToAbsolute(directory, jail, requiredDirectories));

        Assert.Equal(expected.Message, actual.Message);
        Assert.False(path.TryToAbsolute(directory, jail, requiredDirectories, out var tried));
        Assert.Null(tried);
    }

    [Fact]
    public void ToAbsolute_CheckpointsDoNotStrengthenFinalContainmentJail()
    {
        var path = RawShellPath.Parse("../outside/../jail/leaf", null);
        var directory = new ShellPath("/jail");
        var requiredDirectories = new HashSet<ShellPath>();

        var result = path.ToAbsolute(directory, jail: true, requiredDirectories);

        Assert.Equal(path.ToAbsolute(directory, jail: true), result);
        Assert.Equal("/jail/leaf", result.Path);
        Assert.Contains(new ShellPath("/outside"), requiredDirectories);
    }

    [Fact]
    public void ToAbsolute_CheckpointsRetainCallerEntriesAndPartialFailure()
    {
        var existing = new ShellPath("/existing");
        var requiredDirectories = new HashSet<ShellPath> { existing };

        Assert.Throws<ShellPathException>(() => RawShellPath.Parse("a/../..", null)
            .ToAbsolute(ShellPath.Root, jail: false, requiredDirectories));

        Assert.True(requiredDirectories.SetEquals([existing, new ShellPath("/a"), ShellPath.Root]));
    }

    #region ToAbsolute — jail

    [Theory]
    [InlineData("subdir",   "/jail", "/jail/subdir")]   // directly under jail
    [InlineData(".",        "/jail", "/jail")]           // jail itself is allowed
    [InlineData("/jail/sub","/jail", "/jail/sub")]       // absolute but inside jail
    public void ToAbsolute_Jail_AllowsContainedPaths(string raw, string jail, string expected)
    {
        var path = RawShellPath.Parse(raw, null);
        var directory = new ShellPath(jail);
        var result = path.ToAbsolute(directory, jail: true);
        Assert.Equal(expected, result.Path);
        Assert.True(path.TryToAbsolute(directory, jail: true, out var tried));
        Assert.Equal(result, tried);
    }

    [Theory]
    [InlineData("..",       "/jail")]                   // parent escapes
    [InlineData("/other",   "/jail")]                   // different absolute path
    [InlineData("/jailbird", "/jail")]                  // not a path-segment descendant
    [InlineData("../../x",  "/jail/sub")]               // two levels up
    public void ToAbsolute_Jail_ThrowsWhenEscaping(string raw, string jail)
    {
        var ex = Assert.Throws<ShellPathException>(
            () => RawShellPath.Parse(raw, null).ToAbsolute(new ShellPath(jail), jail: true));
        Assert.Contains("escapes jail", ex.Message);
        Assert.False(RawShellPath.Parse(raw, null).TryToAbsolute(new ShellPath(jail), jail: true, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void ToAbsolute_WithoutJail_AllowsLeavingCurrentDirectory()
    {
        var result = RawShellPath.Parse("../foo", null).ToAbsolute(new ShellPath("/bar"), jail: false);
        Assert.Equal("/foo", result.Path);
    }

    [Fact]
    public void ToAbsolute_RootJail_AllowsContainedPaths()
    {
        var path = RawShellPath.Parse("bar/../foo", null);
        Assert.Equal("/foo", path.ToAbsolute(ShellPath.Root, jail: true).Path);
        Assert.True(path.TryToAbsolute(ShellPath.Root, jail: true, out var result));
        Assert.Equal("/foo", result.Path);
    }

    #endregion
}
