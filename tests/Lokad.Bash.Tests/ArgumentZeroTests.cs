using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ArgumentZeroTests
{
    [Theory]
    [InlineData("""bash -c 'printf "<%s><%s>" "$0" "${BASH_ARGV0-unset}"' "fixture name" """, "<fixture name><fixture name>")]
    [InlineData("""export BASH_ARGV0=parent; bash -c 'declare -p BASH_ARGV0' child; printf '<%s>' "$0" """, "declare -x BASH_ARGV0=\"child\"\n<parent>")]
    [InlineData("""BASH_ARGV0=one; BASH_ARGV0+=two; declare BASH_ARGV0+=three; printf '<%s><%s>' "$0" "$BASH_ARGV0" """, "<twothree><twothree>")]
    [InlineData("""BASH_ARGV0=one; declare -n alias=BASH_ARGV0; alias+=two; printf '<%s><%s>' "$0" "$alias" """, "<onetwo><onetwo>")]
    [InlineData("""BASH_ARGV0=base; unset BASH_ARGV0; value=${BASH_ARGV0:=new}; printf '<%s><%s><%s>' "$0" "$value" "$BASH_ARGV0" """, "<base><new><new>")]
    [InlineData("""BASH_ARGV0=; declare -n alias=BASH_ARGV0; value=${alias:=new}; printf '<%s><%s><%s>' "$0" "$value" "$alias" """, "<new><><new>")]
    [InlineData("""BASH_ARGV0=base; f(){ local BASH_ARGV0=local; (declare -g BASH_ARGV0=child; printf '<%s><%s>' "$0" "$BASH_ARGV0"); printf '<%s><%s>' "$0" "$BASH_ARGV0"; }; f; printf '<%s><%s>' "$0" "$BASH_ARGV0" """, "<child><local><base><local><base><base>")]
    [InlineData("""BASH_ARGV0=base; f(){ local -I BASH_ARGV0=local; }; f; printf '<%s><%s>' "$0" "$BASH_ARGV0" """, "<local><local>")]
    [InlineData("""BASH_ARGV0=base; BASH_ARGV0=prefix eval 'declare -g BASH_ARGV0=global; printf "<%s><%s>" "$0" "$BASH_ARGV0"'; printf '<%s><%s>' "$0" "$BASH_ARGV0" """, "<global><prefix><global><global>")]
    [InlineData("""BASH_ARGV0=base; BASH_ARGV0[0]=element; printf '<%s><%s>' "$0" "$BASH_ARGV0" """, "<base><element>")]
    [InlineData("""declare -i BASH_ARGV0=base; for BASH_ARGV0 in "2+3"; do printf '<%s><%s>' "$0" "$BASH_ARGV0"; done""", "<2+3><2+3>")]
    public async Task ActiveBindingAndShellNameFollowTheirOwners(string script, string expected)
    {
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(256 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
                : await host.RunAsync(script, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Fact]
    public async Task ShellNameKeepsRawBytesAfterUnset()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""BASH_ARGV0=$'\xff'; unset BASH_ARGV0; printf '%s' "$0" """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { 0xff }, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public void ShellOwnsNameBytesAfterTheDynamicBindingIsRemoved()
    {
        var variables = Variables.CreateEmpty();
        variables.InitializeArgumentZero();
        var initial = variables.EstimatedMemoryBytes;
        var name = ShellValue.FromText(new string('x', 8192));

        Assert.True(variables.TrySetScalar("BASH_ARGV0", name));
        Assert.Equal(initial + Variables.EstimateValueBytes(name), variables.EstimatedMemoryBytes);
        Assert.True(variables.TryUnset("BASH_ARGV0"));
        Assert.False(variables.TryGetScalar("BASH_ARGV0", out _));
        Assert.Equal(name, variables.ArgumentZero);
        Assert.True(variables.EstimatedMemoryBytes >= Variables.EstimateValueBytes(name));
    }
}
