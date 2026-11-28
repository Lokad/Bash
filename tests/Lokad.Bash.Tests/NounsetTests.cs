using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class NounsetTests
{
    [Theory]
    [InlineData("printf '%s' \"$missing\"", "missing")]
    [InlineData("printf '%s' \"$2\"", "$2")]
    [InlineData("printf '%s' \"${#2}\"", "2")]
    [InlineData("items=([1]=set); printf '%s' \"${items[0]}\"", "items[0]")]
    [InlineData("key=missing; printf '%s' \"${!key}\"", "!key")]
    [InlineData("declare -n ref=missing; printf '%s' \"$ref\"", "ref")]
    [InlineData("printf '%s' \"${missing/word/$(printf unwanted >&2)}\"", "missing")]
    [InlineData("(( missing += 1 ))", "missing")]
    public async Task MissingReadsStopTheReaderBeforeLaterEffects(string read, string subject)
    {
        var result = await EvaluationTestHelper.RunAsync("set -u\nprintf before\n" + read + "\nprintf after");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("before", result.StdOut);
        Assert.Equal($"line 3: {subject}: unbound variable\n", result.StdErr);
    }

    [Fact]
    public async Task GuardsAndNumericAssignmentDistinguishAbsenceFromEmptyValues()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            set -o nounset
            items=([1]=one)
            empty=
            printf '<%s><%s><%s><%s>' "${missing-fallback}" "${empty}" "${#items[0]}" "$((items[0]))"
            printf '<%s><%s>' "${absent[@]}" "${missing:=created}"
            (( count = 2 ))
            [[ -n '' && -n $absent ]]
            printf '<%s><%s>' "$?" "$count"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<fallback><><0><0><created><><1><2>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("read -r value <<EOF\n$missing\nEOF", 0, "status=1")]
    [InlineData("read -r value <<EOF\n$((missing))\nEOF", 1, "")]
    [InlineData("read -r value <<< \"$missing\"", 1, "")]
    public async Task HereInputPreservesTheReachedFailureBoundary(string redirection, int status, string output)
    {
        var result = await EvaluationTestHelper.RunAsync("set -u\n" + redirection + "\nprintf 'status=%s' \"$?\"");

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Equal("line 2: missing: unbound variable\n", result.StdErr);
    }

    [Fact]
    public async Task LocalOptionSnapshotAndCaptureUseTheActiveSetting()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() { local -; set -fu; printf '%s:' "$-"; }
            f
            printf '<%s>' "$missing"
            set -u
            value=$(printf '%s' "$missing")
            printf 'capture=%s:<%s>' "$?" "$value"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fuB:<>capture=1:<>", result.StdOut);
        Assert.Equal("line 5: missing: unbound variable\n", result.StdErr);
    }

    [Fact]
    public async Task HostedLaunchUsesTheOptionAndTheCommandStringExitStatus()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            bash -uc 'printf %s "$missing"' bash
            printf 'short=%s\n' "$?"
            bash -eu -c 'printf %s "$missing"' bash
            printf 'errexit=%s\n' "$?"
            set -u; export SHELLOPTS
            bash +u -c 'printf %s "$missing"' bash
            printf 'imported=%s\n' "$?"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("short=127\nerrexit=1\nimported=127\n", host.StandardOutput.TextContent);
        Assert.Equal(string.Concat(Enumerable.Repeat("bash: line 1: missing: unbound variable\n", 3)),
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }
}
