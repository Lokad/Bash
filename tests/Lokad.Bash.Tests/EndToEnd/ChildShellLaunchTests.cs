namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ChildShellLaunchTests
{
    [Fact]
    public async Task FileLaunchUsesInvocationDirectoryAndPreservesSourceAndArguments()
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "printf wrong\n");
        host.AddFile("/sub/unit", [.. "printf '<%s>' \"$0\" \"$#\" \"$@\" 'L"u8, 0xff, .. "R'\n"u8]);

        var result = await host.RunAsync("cd sub; PWD=/wrong bash ./unit '' $'a\\200b'", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        byte[] expected = [.. "<./unit><2><><a"u8, 0x80, .. "b><L"u8, 0xff, .. "R>"u8];
        Assert.Equal(expected, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CommandStringLaunchPreservesRawSource()
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("bash -c $'printf %s L\\377R'", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { (byte)'L', 0xff, (byte)'R' }, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("bash ./broken 2>errors")]
    [InlineData("bash -c 'printf earlier >effect\nif\n' launcher 2>errors")]
    public async Task ChildReportsLateSyntaxThroughItsDescriptorAfterEarlierEffects(string invocation)
    {
        await using var host = new TestHost();
        host.AddFile("/sub/broken", "printf earlier >effect\nif\n");

        var result = await host.RunAsync("cd sub; " + invocation, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("earlier", host.ReadFile("/sub/effect"));
        Assert.Equal("line 3: syntax error: unexpected end of file\n", host.ReadFile("/sub/errors"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task MissingChildScriptKeepsTheLookupFailureAndReleasesRedirection()
    {
        await using var host = new TestHost();
        host.AddDirectory("/sub");

        var result = await host.RunAsync("cd sub; bash ./missing 2>errors", CancellationToken.None);

        Assert.Equal(127, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("bash: ./missing: No such file or directory\n", host.ReadFile("/sub/errors"));
        Assert.False(host.FileExists("/sub/missing"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ExportFixtureImportsSourcedDefinitionIntoFileLaunch()
    {
        await using var host = new ExportTestHost();
        host.AddFile("/definitions", "f(){ printf '<%s>' \"$0\" \"$@\"; }\n");
        host.AddFile("/sub/unit", "f \"$@\"\n");

        var result = await host.RunAsync(
            "source /definitions; export -f f; cd sub; /usr/bin/bash ./unit 'a b'", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<./unit><a b>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }
}
