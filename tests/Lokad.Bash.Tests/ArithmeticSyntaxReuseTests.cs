using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ArithmeticSyntaxReuseTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  64#_ \n")]
    [InlineData("09")]
    [InlineData("++1")]
    [InlineData("1--2")]
    [InlineData("a+++b")]
    [InlineData("a-- - --b")]
    [InlineData("a=b=3")]
    [InlineData("a+=(a=7,2)")]
    [InlineData("a?b++:c++")]
    [InlineData("0&&(a=08)")]
    [InlineData("1||(a=1/0)")]
    [InlineData("a=1,++b@")]
    [InlineData("a=1,b++@")]
    [InlineData("a=1,b=(c=2)@")]
    [InlineData("a[0]+=b")]
    [InlineData("a[b[0]]++")]
    [InlineData("a=1,b[")]
    [InlineData("a=1,b=2=3")]
    [InlineData("a=1,1 ? b++ : c++")]
    [InlineData("a=1,0 ? b++ : c++")]
    [InlineData("a=1,1 ? b++ @ : c++")]
    [InlineData("a=1,1 ? b++ : c++ @")]
    [InlineData("a=1\r")]
    public async Task PreparedLexicalDecisionsPreserveBindingAndFailureOrder(string expression)
    {
        var source = ShellSource.FromText(expression);
        var dynamic = await TraceAsync(ArithmeticOperand.FromValue(source.ToValue()));
        var prepared = await TraceAsync(ArithmeticOperand.FromSyntax(source, new ArithmeticEvaluation.LexicalSyntax(source)));
        Assert.Equal(dynamic.Result, prepared.Result);
        Assert.Equal(dynamic.Trace, prepared.Trace);

        static async Task<(ArithmeticResult Result, IReadOnlyList<string> Trace)> TraceAsync(ArithmeticOperand operand)
        {
            var values = new Dictionary<string, long> { ["a"] = 1, ["b"] = 2, ["c"] = 3, ["a[0]"] = 5 };
            var trace = new List<string>();
            var result = await ArithmeticEvaluation.EvaluateAsync<string>(operand, Bind, Write);
            return (result, trace);

            ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
            {
                var name = reference.Syntax.ToValue(null).ToUtf8String();
                trace.Add($"bind:{name}:{use}");
                return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Bound(name,
                    use == ArithmeticReferenceUse.Read && values.TryGetValue(name, out var value) ? value : 0));
            }
            ValueTask<ArithmeticError?> Write(ArithmeticReference reference, string target, long value)
            {
                trace.Add($"write:{target}:{value}");
                values[target] = value;
                return ValueTask.FromResult<ArithmeticError?>(null);
            }
        }
    }

    [Fact]
    public async Task SharedSourceKeepsConcurrentBindingStateIndependent()
    {
        var source = new ArithmeticSource(ShellSource.FromText("value + 1"));
        var operand = Assert.IsType<ArithmeticOperand>(source.DirectOperand);
        var firstBinding = new TaskCompletionSource<ArithmeticBinding<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBinding = new TaskCompletionSource<ArithmeticBinding<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ArithmeticEvaluation.EvaluateAsync(operand, (_, _) => new ValueTask<ArithmeticBinding<string>>(firstBinding.Task), NoWrite).AsTask();
        var second = ArithmeticEvaluation.EvaluateAsync(operand, (_, _) => new ValueTask<ArithmeticBinding<string>>(secondBinding.Task), NoWrite).AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        secondBinding.SetResult(new ArithmeticBinding<string>.Bound("second", 40));
        firstBinding.SetResult(new ArithmeticBinding<string>.Bound("first", 4));
        Assert.Equal(new ArithmeticResult(5, null), await first);
        Assert.Equal(new ArithmeticResult(41, null), await second);

        static ValueTask<ArithmeticError?> NoWrite(ArithmeticReference reference, string target, long value) =>
            throw new InvalidOperationException("Unexpected write");
    }

    [Fact]
    public async Task ReusingAParsedScriptReadsFreshPositionalAndVariableState()
    {
        var script = Parser.Parse("sum=$1; for ((i=0;i<5;i++)); do ((sum+=i)); done; printf '%s' \"$sum\"");
        foreach (var start in new[] { 0, 100, -20 })
        {
            await using var host = new TestHost();
            var evaluation = new Evaluation.Evaluation(script);
            evaluation.ReplacePositionalParameters([ShellValue.FromText(start.ToString())]);
            var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal((start + 10).ToString(), host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
        }
    }

    [Theory]
    [InlineData("""i=0; j=0; while ((i<3)); do printf '%s:%s|' "$((++i))" "$((j+=i))"; done""", "1:1|2:3|3:6|")]
    [InlineData("""printf '%s|%s' '$((9/0))' "\$((9/0))";""", "$((9/0))|$((9/0))")]
    [InlineData("""value=kept; i=0; printf '%s:%s' "${value:-$((++i))}" "$i";""", "kept:0")]
    [InlineData("""for expr in '1+2' '3*4'; do printf '%s|' "$(($expr))"; done""", "3|12|")]
    [InlineData("""a=(10 20); i=0; printf '%s:%s:%s' "$((a[i++]+=3))" "${a[0]}" "$i";""", "13:13:1")]
    [InlineData("""value=100; f() { local value=$1; printf '%s|' "$((++value))"; }; f 2; f 7; printf '%s' "$value";""", "3|8|100")]
    [InlineData("""i=2; printf '%s:%s' "$((1+$((i++))))" "$i";""", "3:3")]
    public async Task ReusedExpansionSyntaxPreservesQuotationLazinessAndFreshBindings(string source, string expected)
    {
        var script = Parser.Parse(source);
        for (var repetition = 0; repetition < 2; repetition++)
        {
            await using var host = new TestHost();
            var result = await new Engine().RunAsync(new Evaluation.Evaluation(script), host, CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
        }
    }

    [Fact]
    public void RecordCopiesRebuildSyntaxOnlyForChangedExpressions()
    {
        var expression = ShellSource.FromText("value+1");
        var command = new ArithmeticCommand(Expression: expression);
        var same = command with { };
        Assert.Equal(command, same);
        Assert.Equal(command.GetHashCode(), same.GetHashCode());
        Assert.Same(command.Syntax, same.Syntax);
        same.Deconstruct(out var originalSource);
        Assert.True(expression.Bytes.Equals(originalSource.Bytes));

        var changed = command with { Expression = ShellSource.FromText("7") };
        Assert.NotEqual(command, changed);
        Assert.NotSame(command.Syntax, changed.Syntax);
        Assert.Equal(7, changed.Syntax.Syntax?.LiteralValue);

        var body = Assert.Single(Parser.Parse(":").Commands);
        var loop = new ArithmeticForCommand(ShellSource.FromText("i=0;i<3;i++"), body);
        var loopCopy = loop with { Body = body };
        Assert.Equal(loop, loopCopy);
        Assert.Equal(loop.GetHashCode(), loopCopy.GetHashCode());
        Assert.Same(loop.Condition, loopCopy.Condition);
        var changedLoop = loop with { Expression = ShellSource.FromText("i=0;0;i++") };
        Assert.NotEqual(loop, changedLoop);
        Assert.Equal(0, changedLoop.Condition.Syntax?.LiteralValue);
        Assert.NotSame(loop.Condition, changedLoop.Condition);
        changedLoop.Deconstruct(out var clauses, out var copiedBody);
        Assert.Equal("i=0;0;i++", clauses.ToUtf8String());
        Assert.Same(body, copiedBody);
    }
}
