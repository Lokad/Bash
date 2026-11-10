using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ShellArgumentEscapingTests
{
    [Theory]
    [InlineData("", "''")]
    [InlineData("plain-text", "plain-text")]
    [InlineData("/tmp/file.txt", "/tmp/file.txt")]
    [InlineData("hello world", "'hello world'")]
    [InlineData("it's", "'it'\"'\"'s'")]
    public void EscapeArgument_FormatsArgumentForShell(string argument, string expected)
    {
        var actual = ShellArgumentEscaping.EscapeArgument(argument);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void JoinArguments_FormatsCommandLine()
    {
        var actual = ShellArgumentEscaping.JoinArguments(["python", "-c", "print('hi there')"]);

        Assert.Equal("python -c 'print('\"'\"'hi there'\"'\"')'", actual);
    }
}
