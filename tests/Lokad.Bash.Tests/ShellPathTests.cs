using System.Text.Json;

namespace Lokad.Bash.Tests;

public sealed class ShellPathTests
{
    #region Serialization round-trips

    [Fact]
    public void SystemTextJson_RoundTrip()
    {
        var path = new ShellPath("/foo/bar");
        var json = JsonSerializer.Serialize(path);
        var restored = JsonSerializer.Deserialize<ShellPath>(json);
        Assert.Equal(path, restored);
    }

    [Fact]
    public void SystemTextJson_SerializesAsString()
    {
        Assert.Equal("\"/foo/bar\"", JsonSerializer.Serialize(new ShellPath("/foo/bar")));
    }

    #endregion

    #region Parent

    [Fact]
    public void Parent_ReturnsNullForRoot()
    {
        Assert.Null(ShellPath.Root.Parent);
    }

    [Theory]
    [InlineData("/foo", "/")]
    [InlineData("/foo/bar", "/foo")]
    [InlineData("/foo/bar/baz", "/foo/bar")]
    public void Parent_ReturnsParentPath(string path, string parent)
    {
        Assert.Equal(new ShellPath(parent), new ShellPath(path).Parent);
    }

    #endregion

    #region Valid paths

    [Theory]
    [InlineData("/")]
    [InlineData("/foo")]
    [InlineData("/foo/bar")]
    [InlineData("/foo/bar/baz")]
    [InlineData("/with-dash")]
    [InlineData("/with.dot.in.name")]
    [InlineData("/with_underscore")]
    [InlineData("/unicode/路径")]
    [InlineData("/emoji/🚀")]
    [InlineData("/colon:allowed")]
    [InlineData("/percent%20")]
    [InlineData("/at@sign")]
    [InlineData("/plus+sign")]
    [InlineData("/comma,here")]
    [InlineData("/foo/with space")]
    [InlineData("/foo/'single'")]
    [InlineData("/foo/*")]
    [InlineData("/foo/?")]
    [InlineData("/foo/[bar]")]
    [InlineData("/foo/{a,b}")]
    [InlineData("/foo/~")]
    [InlineData("/foo/a|b")]
    [InlineData("/foo/a&b")]
    [InlineData("/foo/a;b")]
    [InlineData("/foo/a<b")]
    [InlineData("/foo/a>b")]
    [InlineData("/foo/(sub)")]
    [InlineData("/foo/$var")]
    [InlineData("/foo/`cmd`")]
    [InlineData("/foo/back\\slash")]
    [InlineData("/foo/\"quoted\"")]
    public void IsValid_AcceptsValidPaths(string path)
    {
        Assert.True(ShellPath.IsValid(path));
        var _ = new ShellPath(path); // must not throw
    }

    #endregion

    #region Must start with '/'

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("no-slash")]
    [InlineData("C:\\xyz")]
    [InlineData("\\\\xyz")]
    public void IsValid_RejectsPathsNotStartingWithSlash(string path)
    {
        Assert.False(ShellPath.IsValid(path));
        Assert.Throws<ArgumentException>(() => new ShellPath(path));
    }

    #endregion

    #region Double slash

    [Theory]
    [InlineData("//")]
    [InlineData("//foo")]
    [InlineData("/foo//bar")]
    [InlineData("/foo//")]
    public void IsValid_RejectsDoubleSlash(string path)
    {
        Assert.False(ShellPath.IsValid(path));
    }

    #endregion

    #region Trailing slash

    [Theory]
    [InlineData("/foo/")]
    [InlineData("/foo/bar/")]
    public void IsValid_RejectsTrailingSlash(string path)
    {
        Assert.False(ShellPath.IsValid(path));
    }

    #endregion

    #region Dot and dot-dot segments

    [Theory]
    [InlineData("/foo/./bar")]
    [InlineData("/./foo")]
    [InlineData("/foo/.")]
    [InlineData("/foo/../bar")]
    [InlineData("/../foo")]
    [InlineData("/foo/..")]
    public void IsValid_RejectsDotSegments(string path)
    {
        Assert.False(ShellPath.IsValid(path));
    }

    [Theory]
    [InlineData("/foo/.hidden")]
    [InlineData("/foo/..startwithdots")]
    [InlineData("/foo/file.txt")]
    public void IsValid_AcceptsSegmentsWithLeadingDots(string path)
    {
        Assert.True(ShellPath.IsValid(path));
    }

    #endregion

    #region Forbidden characters

    [Theory]
    [InlineData("/foo/tab\there")]
    [InlineData("/foo/newline\nhere")]
    [InlineData("/foo/\x01control")]
    public void IsValid_RejectsForbiddenCharacters(string path)
    {
        Assert.False(ShellPath.IsValid(path));
    }

    #endregion
}
