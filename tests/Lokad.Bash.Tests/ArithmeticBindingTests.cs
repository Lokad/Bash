using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ArithmeticBindingTests
{
    [Fact]
    public async Task Binding_ResumesAtMaximumNestingDepth()
    {
        var depth = ShellSourceCursor.MaximumNestedExpansionDepth;
        var expression = new string('(', depth) + "value" + new string(')', depth);
        var selected = new TaskCompletionSource<ArithmeticBinding<string>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = ArithmeticEvaluation.EvaluateAsync<string>(
            ArithmeticOperand.FromValue(ShellValue.FromText(expression)), Bind,
            (_, _, _) => throw new InvalidOperationException("Unexpected write")).AsTask();

        Assert.False(pending.IsCompleted);
        selected.SetResult(new ArithmeticBinding<string>.Bound("value", 7));
        Assert.Equal(new ArithmeticResult(7, null), await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            Assert.Equal("value", reference.Syntax.Name.ToValue().ToUtf8String());
            Assert.Equal(ArithmeticReferenceUse.Read, use);
            return new ValueTask<ArithmeticBinding<string>>(selected.Task);
        }
    }

    [Fact]
    public async Task Assignment_SelectsTargetAfterRightHandEffects()
    {
        var operations = new List<string>();

        var result = await ArithmeticEvaluation.EvaluateAsync<string>(
            ArithmeticOperand.FromValue(ShellValue.FromText("left=(right=1,7)")), Bind, Write);

        Assert.Equal(new ArithmeticResult(7, null), result);
        Assert.Equal(["bind:right:Assign", "write:right:1", "bind:left:Assign", "write:left:7"], operations);

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            operations.Add($"bind:{reference.Syntax.Name.ToValue().ToUtf8String()}:{use}");
            return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Bound(reference.Syntax.Name.ToValue().ToUtf8String(), 0));
        }

        ValueTask<ArithmeticError?> Write(ArithmeticReference reference, string target, long value)
        {
            operations.Add($"write:{target}:{value}");
            return ValueTask.FromResult<ArithmeticError?>(null);
        }
    }

    [Fact]
    public async Task CompoundAssignment_AwaitsReadAndRetainsItsSelectedTarget()
    {
        var selected = new TaskCompletionSource<ArithmeticBinding<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<KeyValuePair<string, long>>();
        var leftBindings = 0;
        var pending = ArithmeticEvaluation.EvaluateAsync<string>(
            ArithmeticOperand.FromValue(ShellValue.FromText("left+=(right=1,7)")), Bind, Write).AsTask();

        Assert.False(pending.IsCompleted);
        Assert.Empty(writes);
        selected.SetResult(new ArithmeticBinding<string>.Bound("selected-left", 10));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new ArithmeticResult(17, null), result);
        Assert.Equal(1, leftBindings);
        Assert.Equal([KeyValuePair.Create("right", 1L), KeyValuePair.Create("selected-left", 17L)], writes);

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            if (reference.Syntax.Name.ToValue().ToUtf8String() == "left")
            {
                Assert.Equal(ArithmeticReferenceUse.Read, use);
                leftBindings++;
                return new ValueTask<ArithmeticBinding<string>>(selected.Task);
            }
            Assert.Equal(ArithmeticReferenceUse.Assign, use);
            return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Bound(reference.Syntax.Name.ToValue().ToUtf8String(), 0));
        }

        ValueTask<ArithmeticError?> Write(ArithmeticReference reference, string target, long value)
        {
            writes.Add(KeyValuePair.Create(target, value));
            return ValueTask.FromResult<ArithmeticError?>(null);
        }
    }

    [Fact]
    public async Task Assignment_AwaitsPublicationBeforeReadingAgain()
    {
        var published = new TaskCompletionSource<ArithmeticError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var stored = 0L;
        var pending = ArithmeticEvaluation.EvaluateAsync<string>(
            ArithmeticOperand.FromValue(ShellValue.FromText("value=2,value+1")), Bind, Write).AsTask();

        Assert.False(pending.IsCompleted);
        Assert.Equal(0, reads);
        stored = 2;
        published.SetResult(null);
        Assert.Equal(new ArithmeticResult(3, null), await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, reads);

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            if (use == ArithmeticReferenceUse.Read)
                reads++;
            return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Bound(reference.Syntax.Name.ToValue().ToUtf8String(), stored));
        }

        ValueTask<ArithmeticError?> Write(ArithmeticReference reference, string target, long value)
        {
            Assert.Equal("value", target);
            Assert.Equal(2, value);
            return new ValueTask<ArithmeticError?>(published.Task);
        }
    }

    [Fact]
    public async Task BindingFailure_StopsBeforeRightHandEffects()
    {
        var failure = new ArithmeticError(ArithmeticErrorKind.InvalidTarget, ShellValue.FromText("left"));
        var result = await ArithmeticEvaluation.EvaluateAsync<string>(
            ArithmeticOperand.FromValue(ShellValue.FromText("left+=(right=1)")), Bind,
            (_, _, _) => throw new InvalidOperationException("Unexpected write"));

        Assert.Equal(new ArithmeticResult(0, failure), result);

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            Assert.Equal("left", reference.Syntax.Name.ToValue().ToUtf8String());
            return ValueTask.FromResult<ArithmeticBinding<string>>(new ArithmeticBinding<string>.Failure(failure));
        }
    }

    [Fact]
    public async Task UnselectedOperand_DoesNotBindOrWrite()
    {
        var result = await ArithmeticEvaluation.EvaluateAsync<string>(
            ArithmeticOperand.FromValue(ShellValue.FromText("1 || left++")),
            (_, _) => throw new InvalidOperationException("Unselected binding"),
            (_, _, _) => throw new InvalidOperationException("Unselected write"));

        Assert.Equal(new ArithmeticResult(1, null), result);
    }

    [Fact]
    public async Task AssignmentFailure_AwaitsPublicationAndSkipsLaterBindings()
    {
        var published = new TaskCompletionSource<ArithmeticError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new ArithmeticError(ArithmeticErrorKind.ReadonlyVariable, ShellValue.FromText("target"));
        var pending = ArithmeticEvaluation.EvaluateAsync<string>(
            ArithmeticOperand.FromValue(ShellValue.FromText("target=1,later")), Bind,
            (_, _, _) => new ValueTask<ArithmeticError?>(published.Task)).AsTask();

        Assert.False(pending.IsCompleted);
        published.SetResult(failure);

        Assert.Equal(new ArithmeticResult(0, failure), await pending.WaitAsync(TimeSpan.FromSeconds(5)));

        ValueTask<ArithmeticBinding<string>> Bind(ArithmeticReference reference, ArithmeticReferenceUse use)
        {
            Assert.Equal("target", reference.Syntax.Name.ToValue().ToUtf8String());
            Assert.Equal(ArithmeticReferenceUse.Assign, use);
            return ValueTask.FromResult<ArithmeticBinding<string>>(
                new ArithmeticBinding<string>.Bound(reference.Syntax.Name.ToValue().ToUtf8String(), 0));
        }
    }
}
