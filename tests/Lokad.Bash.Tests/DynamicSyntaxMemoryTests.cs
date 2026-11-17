using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DynamicSyntaxMemoryTests
{
    [Theory]
    [InlineData("direct", false)]
    [InlineData("direct", true)]
    [InlineData("eval", false)]
    [InlineData("eval", true)]
    [InlineData("source", false)]
    [InlineData("source", true)]
    [InlineData("trap", false)]
    [InlineData("trap", true)]
    public async Task UnexpandedNamerefSelectorsShareTheActivePreparationAllowance(string route, bool prepared)
    {
        var denseTarget = "a[$(" + string.Concat(Enumerable.Repeat(":;", 4096)) + ": >effect; printf 0)]";
        var sparseTarget = "a[0" + new string(' ', denseTarget.Length - 4) + "]";
        Assert.Equal(denseTarget.Length, sparseTarget.Length);
        foreach (var dense in new[] { false, true })
        {
            var target = dense ? denseTarget : sparseTarget;
            var body = "declare -n p=original; declare -n p='" + target
                + "'; printf '|%s|%s|tail' \"$?\" \"${!p}\"";
            var source = route switch
            {
                "direct" => body,
                "eval" => "eval " + Quote(body),
                "source" => ". ./unit",
                "trap" => "trap " + Quote(body) + " EXIT; :",
                _ => throw new ArgumentOutOfRangeException(nameof(route))
            };
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            if (route == "source")
                host.AddFile("/unit", body);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token)
                : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(dense ? "|1|original|tail" : "|0|" + target + "|tail", host.StandardOutput.TextContent);
            Assert.Equal(dense, host.StandardError.TextContent.Contains("shared memory budget exhausted", StringComparison.Ordinal));
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/effect"));
            Assert.Equal(0, host.OpenFileCount);
            if (!prepared)
                Assert.Equal(0, governor.ReservedBytes); // Runtime retirement precedes caller scope disposal.
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }

        static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    public async Task ImportedNamespacesAreAdmittedBeforeExecution(int count)
    {
        var evaluation = new Eval(Parser.Parse("printf ready"));
        for (var index = 0; index < count; index++)
            evaluation.SetExportedFunction(Assert.IsType<FunctionDefinition>(
                Assert.Single(Parser.Parse("f" + index + "(){ :; }").Commands)));
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, cancellation.Token);

        Assert.Equal(count == 0 ? 0 : 1, result.ExitCode);
        Assert.Equal(count == 0 ? "ready" : "", host.StandardOutput.TextContent);
        Assert.Equal(count != 0, host.StandardError.TextContent.Contains("shared memory budget exhausted", StringComparison.Ordinal));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RuntimeFunctionContainersAdmitBeforeGrowingTheNamespace()
    {
        var source = "set -e; " + string.Concat(Enumerable.Range(0, 1024).Select(index => "f" + index + "(){ :; }; "))
            + "printf unreachable";
        var script = Parser.Parse(source);
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(new Eval(script), host, owner, cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ExitParserRefusalUsesTheActiveDiagnosticDescriptorBeforeCleanup()
    {
        var expression = string.Concat(Enumerable.Repeat("0+", 8192)) + "0";
        var source = "trap 'f(){ (( " + expression + " )); }; printf unreachable' EXIT; exit 7 2>error";
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Contains("shared memory budget exhausted", host.ReadFile("/error"));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("f(){ (( EXPR )); }")]
    [InlineData("f(){ for ((x=EXPR;0;)); do :; done; }")]
    [InlineData("f(){ : $(( EXPR )); }")]
    [InlineData("f(){ : \"$(( EXPR ))\"; }")]
    [InlineData("f(){ a=$(( EXPR )); }")]
    [InlineData("f(){ : >&$(( EXPR ))-; }")]
    [InlineData("f(){ [[ $(( EXPR )) == 0 ]]; }")]
    [InlineData("f(){ : <<END\n$(( EXPR ))\nEND\n}")]
    public async Task RetainedSyntaxDensityUsesTheDynamicParseAllowance(string template)
    {
        var expression = string.Concat(Enumerable.Repeat("0+", 8192)) + "0";
        await AssertDynamicDensityAsync(template.Replace("EXPR", expression, StringComparison.Ordinal),
            template.Replace("EXPR", "0" + new string(' ', expression.Length - 1), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("conditional")]
    [InlineData("commands")]
    [InlineData("references")]
    public async Task NonLiteralSyntaxContainersShareTheSameAdmissionBoundary(string kind)
    {
        var body = kind switch
        {
            "conditional" => "f(){ [[ " + string.Concat(Enumerable.Repeat("x || ", 4096)) + "x ]]; }",
            "commands" => "f(){ " + string.Concat(Enumerable.Repeat(":; ", 4096)) + "}",
            "references" => "f(){ (( " + string.Concat(Enumerable.Repeat("a[0]+", 4096)) + "0 )); }",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var control = "f(){ :" + new string(' ', body.Length - 9) + "; }";
        await AssertDynamicDensityAsync(body, control);
    }

    private static async Task AssertDynamicDensityAsync(string denseBody, string controlBody)
    {
        Assert.Equal(controlBody.Length, denseBody.Length);
        foreach (var dense in new[] { false, true })
        {
            var body = dense ? denseBody : controlBody;
            var source = "eval '" + body + "; printf instantiated'; result=$?; printf '|%s|tail' \"$result\"";
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(dense ? "|1|tail" : "instantiated|0|tail", host.StandardOutput.TextContent);
            Assert.Equal(dense, host.StandardError.TextContent.Contains("shared memory budget exhausted", StringComparison.Ordinal));
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacedSyntaxMetadataRetiresWithoutConsumingAnExecutionQuota(bool unset)
    {
        var expression = string.Concat(Enumerable.Repeat("0+", 64)) + "0";
        var source = "for ((i=0;i<2049;i++)); do eval 'f(){ (( " + expression + " )); }'; "
            + (unset ? "unset -f f; " : "") + "done; unset -f f; printf ready";
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("source")]
    [InlineData("eval")]
    [InlineData("trap")]
    [InlineData("trap-zero")]
    [InlineData("trap-explicit")]
    [InlineData("modern")]
    [InlineData("legacy")]
    public async Task EachRuntimeParserAdmitsItsArithmeticMetadata(string route)
    {
        var expression = string.Concat(Enumerable.Repeat("0+", 8192)) + "0";
        var body = "f(){ (( " + expression + " )); }; printf instantiated";
        var source = route switch
        {
            "root" => body,
            "source" => ". ./unit",
            "eval" => "eval '" + body + "'",
            "trap" => "set -e; trap '" + body + "' EXIT; false",
            "trap-zero" => "trap '" + body + "' EXIT; :",
            "trap-explicit" => "trap '" + body + "' EXIT; exit 7",
            "modern" => "x=$( " + body + " )",
            "legacy" => "x=`" + body + "`",
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        if (route == "source") host.AddFile("/unit", body);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task CallerOwnedPreparedSyntaxDoesNotAcquireRuntimeParserAdmission()
    {
        var expression = string.Concat(Enumerable.Repeat("0+", 8192)) + "0";
        var script = Parser.Parse("f(){ (( " + expression + " )); }; printf ready");
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(new Eval(script), host, owner, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task UnreachedLegacyBodiesKeepTheirDeferredParseLifetime()
    {
        var expression = string.Concat(Enumerable.Repeat("0+", 8192)) + "0";
        var source = "f(){ : `g(){ (( " + expression + " )); }; printf nested`; }; printf ready";
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrowingEvalDefinitionsRefuseBeforeRetainedSourceEscapesTheAllowance(bool prepared)
    {
        const int Count = 1024;
        var source = "payload='" + new string('x', 4096) + "'; i=0; while ((i<1024)); do "
            + "eval \"f$i(){ : '$payload'; }\" || break; ((i+=1)); done; printf '%s' \"$i\"";
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.InRange(int.Parse(host.StandardOutput.TextContent, System.Globalization.CultureInfo.InvariantCulture), 1, Count - 1);
        Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent);
        Assert.InRange(governor.PeakBytes, 1, governor.LimitBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetiredDynamicDefinitionsReuseTheirAllowance(bool prepared, bool unset)
    {
        const int Count = 8193;
        var source = "payload='" + new string('x', 4096) + "'; i=0; while ((i<8193)); do "
            + "eval \"f(){ : '$payload'; }\" || break; " + (unset ? "unset -f f; " : "")
            + "((i+=1)); done; unset -f f; printf '%s' \"$i\"";
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        // Keep the non-cumulative 8,193-iteration boundary. This verifies live ownership;
        // a generous stall guard leaves throughput independent of concurrent CI work.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Count.ToString(System.Globalization.CultureInfo.InvariantCulture), host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedUnitsReleaseSyntaxEvenWhenAnEarlierDefinitionSurvives(bool prepared, bool retain)
    {
        var code = (retain ? "f(){ :; }\n" : "") + string.Concat(Enumerable.Repeat(":\n", 8193));
        var source = "eval '" + code + "'; printf ready" + (retain ? "; f; unset -f f" : "");
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
