using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class LineNumberTests
{
    [Theory]
    [InlineData("LINENO=42 after=$LINENO\nprintf '<%s>' \"$after\"\nprintf '<%s>' \"$LINENO\"", "<42><3>")]
    [InlineData("unset LINENO\nLINENO=42\nprintf '<%s>' \"$LINENO\"", "<42>")]
    [InlineData("f(){\nlocal LINENO=84\nprintf '<%s>' \"$LINENO\"\n}\nf\nprintf '<%s>' \"$LINENO\"", "<84><6>")]
    [InlineData("declare -n location=LINENO\n\nprintf '<%s>' \"$location\"", "<3>")]
    [InlineData("f(){\nprintf '<%s>' \"$LINENO\"\n}\nLINENO=42 f\nprintf '<%s>' \"$LINENO\"", "<42><5>")]
    [InlineData("\n[[ $LINENO = 2 &&\n $LINENO = 2 ]] && printf opening || printf later", "later")]
    [InlineData("\n(( LINENO == 2 \n )) && printf opening || printf later", "later")]
    [InlineData("\n\neval 'printf \"<%s>\" \"$LINENO\"\nprintf \"<%s>\" \"$LINENO\"'\nprintf '<%s>' \"$LINENO\"", "<4><5><5>")]
    [InlineData("trap 'printf \"<%s>\" \"$LINENO\"\nprintf \"<%s>\" \"$LINENO\"' EXIT\n\n: done", "<1><2>")]
    public Task BindingFollowsTheActiveCommandAndScope(string script, string expected) =>
        CheckBothEntryPointsAsync(script, expected);

    [Theory]
    [InlineData("x=$(\nprintf '<%s>' \"$LINENO\"\n\n# skipped\n\nprintf '<%s>' \"$LINENO\"\n)", "[8:<7><8>]")]
    [InlineData("x=`\nprintf '<%s>' \"$LINENO\"\n\n# skipped\n\nprintf '<%s>' \"$LINENO\"\n`", "[8:<8><12>]")]
    [InlineData("x=`\nprintf '<%s>' \\\n \"$LINENO\"\nprintf '<%s>' \"$LINENO\"\n`", "[6:<6><7>]")]
    [InlineData("x=$(if true; then printf '<%s>' \"$LINENO\"; else :; fi; printf '<%s>' \"$LINENO\")", "[2:<2><5>]")]
    [InlineData("x=$(\ncat <<EOF\n$LINENO\nEOF\nprintf '<%s>' \"$LINENO\"\n)", "[7:6\n<10>]")]
    [InlineData("x=$(f(){\nprintf '<%s>' \"$LINENO\"\nprintf '<%s>' \"$LINENO\";\nprintf '<%s>' \"$LINENO\"\n}; f; printf '<%s>' \"$LINENO\")", "[6:<7><9><10><11>]")]
    [InlineData("x=$(printf '[%s]' \"${missing:-$(\n\nprintf '<%s>' \"$LINENO\"\n\nprintf '<%s>' \"$LINENO\")}\"\nprintf '<%s>' \"$LINENO\")", "[7:[<6><7>]<8>]")]
    [InlineData("x=$(printf '[%s]' \"$((1 + $(\n\nprintf %s \"$LINENO\"\n)))\"\nprintf '<%s>' \"$LINENO\")", "[6:[6]<6>]")]
    [InlineData("x=$([[ x == x &&\n y == y ]]; printf '<%s>' \"$LINENO\")", "[3:<2>]")]
    public Task CapturesFollowTheCoordinatesOfTheirOwnBody(string script, string expected) =>
        CheckBothEntryPointsAsync(script + "\nprintf '[%s:%s]' \"$LINENO\" \"$x\"", expected);

    [Fact]
    public Task FunctionKeepsItsDefiningSourceAndCallerResumesItsOwn() =>
        CheckBothEntryPointsAsync("source fixture\n\nrun\nprintf '<%s>' \"$LINENO\"", "<3><4>",
            host => host.AddFile("/fixture", "\nrun(){\n  printf '<%s>' \"$LINENO\"\n}\n"));

    [Fact]
    public Task FirstCompleteRedirectionSetsTheSimpleCommandLine() =>
        CheckBothEntryPointsAsync(">\\\n out printf '<%s>' \"$LINENO\"", "",
            check: host => Assert.Equal("<2>", host.ReadFile("/out")));

    private static async Task CheckBothEntryPointsAsync(string script, string expected,
        Action<TestHost>? setup = null, Action<TestHost>? check = null)
    {
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(256 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            setup?.Invoke(host);
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
                : await host.RunAsync(script, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
            check?.Invoke(host);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }
}
