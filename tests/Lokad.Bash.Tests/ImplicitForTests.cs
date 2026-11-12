using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ImplicitForTests
{
    [Theory]
    [InlineData("for x; do :; done")]
    [InlineData("for x do :; done")]
    [InlineData("for x\ndo :; done")]
    public void ImplicitListUsesTheExistingQuotedPositionalWord(string source)
    {
        var loop = Assert.IsType<ForCommand>(Assert.Single(Parser.Parse(source).Commands));
        var part = Assert.IsType<SourceWordPart>(Assert.Single(Assert.Single(loop.Items).Parts));
        Assert.Equal(WordPartKind.DoubleQuoted, part.Kind);
        Assert.Equal("$@", part.Source.ToUtf8String());
    }

    [Fact]
    public async Task ImplicitListPreservesEmptyByteValuesAndSnapshotsBeforeMutation()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("IFS=:; set -- '' 'a::b' '*' $'\\377'; "
            + "for x; do printf '<%s>' \"$x\"; shift; set -- changed; done; printf ':%s:%s' \"$#\" \"$1\"",
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString("3c3e3c613a3a623e3c2a3e3cff3e3a313a6368616e676564"),
            host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }
}
