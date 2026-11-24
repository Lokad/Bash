using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class LastArgumentTests
{
    [Theory]
    [InlineData("v='first last'; : $v; printf '<%s>' \"$_\"", "<last>", 0)]
    [InlineData("v='first last'; : \"$v\"; printf '<%s>' \"$_\"", "<first last>", 0)]
    [InlineData("unset missing; true $missing; printf '<%s>' \"$_\"", "<true>", 0)]
    [InlineData(": first ''; printf '<%s>' \"$_\"; _=manual; printf '<%s>' \"$_\"", "<><>", 0)]
    [InlineData("f(){ printf '<%s>' \"$_\"; : body; }; : earlier; f call; printf '<%s>' \"$_\"", "<earlier><call>", 0)]
    [InlineData(": outer; (: inner); : first | : last; printf '<%s>' \"$_\"", "<outer>", 0)]
    [InlineData("declare -a v=(one two); printf '<%s>' \"$_\"; command --; printf '<%s>' \"$_\"", "<v><-->", 0)]
    [InlineData("builtin declare v='two words'; printf '<%s:%s>' \"$_\" \"$v\"", "<v=two words:two words>", 0)]
    [InlineData("v=old; declare v+=; printf '<%s:%s>' \"$_\" \"$v\"", "<v+=:old>", 0)]
    [InlineData("declare -a scratch=(one) v+='* $x $(ghost)'; printf '<%s>' \"$_\"", "<v+=* $x $(ghost)>", 0)]
    [InlineData("f(){ local; printf '<%s>' \"$_\"; }; f", "<local>", 0)]
    [InlineData("builtin test; printf '<%s:%s>' \"$?\" \"$_\"", "<1:test>", 0)]
    [InlineData("declare -n _=target; : tail; printf '<%s:%s>' \"$_\" \"$target\"", "<tail:tail>", 0)]
    [InlineData("trap 'printf \"<%s>\" \"$_\"' EXIT; set -e; false last", "<last>", 1)]
    [InlineData("trap 'printf \"<%s>\" \"$_\"' EXIT; : prior; exit 7", "<prior>", 7)]
    public async Task CompletedCommandsPublishTheirLastExpandedArgument(string script, string output, int status)
    {
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(256 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            var actual = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
                : await host.RunAsync(script, CancellationToken.None);

            Assert.Equal(status, actual.ExitCode);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Fact]
    public async Task RedirectionReadsThePriorArgumentAndTheNewBindingPreservesBytes()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            : target
            : $'\377\r' >"$_"
            printf '%s' "$_"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { 0xff, 0x0d }, host.StandardOutput.Content.ToArray());
        Assert.Equal("target", Assert.Single(host.CaptureEntries()).Key);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ReadonlyLastArgumentKeepsThePreviousBinding()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            : prior; readonly _
            : tail
            printf '<%s>' "$_"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<prior>", host.StandardOutput.TextContent);
        Assert.Equal(string.Concat(Enumerable.Repeat("_: readonly variable\n", 3)), host.StandardError.TextContent);
    }

    [Fact]
    public async Task LastArgumentRetainsOnlyItsByteSlice()
    {
        var backing = new byte[16 * 1024];
        var payload = backing.AsMemory(1024, 2048);
        payload.Span.Fill(0xff);
        var state = new Eval(Parser.Parse(": \"$1\""));
        state.ReplacePositionalParameters([ShellValue.FromBytes(payload)]);
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var retained = state.Variables["_"];
        Assert.True(retained.Bytes.Span.SequenceEqual(payload.Span));
        Assert.True(retained.TryGetCompactArrayCapacity(out var capacity));
        Assert.Equal(payload.Length, capacity);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
