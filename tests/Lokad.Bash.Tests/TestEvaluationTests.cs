using System.Collections.Generic;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class TestEvaluationTests
{
    [Theory]
    [InlineData("-eq\0")]
    [InlineData("=\0")]
    [InlineData("-\u00e9")]
    public void OperatorRecognitionRequiresExactAscii(string op)
    {
        Assert.True(TestEvaluation.Parse(["1", op, "1"]).HasParseError);
    }

    [Fact]
    public void TestEvaluation_SingleOperatorLookingArgumentIsANonemptyString()
    {
        var evaluation = TestEvaluation.Parse([
            "-z"
        ]);

        Assert.False(evaluation.HasParseError);
        evaluation.CollectFileTests(out var result);
        Assert.True(result);
    }

    [Fact]
    public void TestEvaluation_EvaluatesNumericPredicate()
    {
        var evaluation = TestEvaluation.Parse([
            "1",
            "-eq",
            "1"
        ]);

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.True(result.Value);
    }

    [Fact]
    public void TestEvaluation_CollectsFileTestsAndCompletes()
    {
        var evaluation = TestEvaluation.Parse([
            "-f",
            "file",
            "-o",
            "-d",
            "dir"
        ]);

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Null(result);
        Assert.Equal(2, fileTests.Count);
        Assert.Equal(FilePredicateKind.File, fileTests[0].Kind);
        Assert.Equal("file", fileTests[0].Operand);
        Assert.Equal(FilePredicateKind.Directory, fileTests[1].Kind);
        Assert.Equal("dir", fileTests[1].Operand);

        var finalResult = evaluation.CompleteFileTests([false, true]);
        Assert.True(finalResult);
    }

    [Fact]
    public void TestEvaluation_RespectsOperatorPrecedence()
    {
        var evaluation = TestEvaluation.Parse([
            "-n",
            "a",
            "-a",
            "-n",
            "",
            "-o",
            "-n",
            "b"
        ]);

        var fileTests = evaluation.CollectFileTests(out var result);
        Assert.Empty(fileTests);
        Assert.True(result.HasValue);
        Assert.True(result.Value);
    }

    [Fact]
    public void TestEvaluation_AllocatesReductionScratchForLiveDepth()
    {
        const int Leaves = 64;
        const int Repetitions = 128;
        var flat = TestEvaluation.Parse(string.Join(" -a ", Enumerable.Repeat("yes", Leaves)).Split(' '));
        var nested = TestEvaluation.Parse((string.Concat(Enumerable.Repeat("yes -a ( ", Leaves - 1))
            + "yes" + string.Concat(Enumerable.Repeat(" )", Leaves - 1))).Split(' '));

        var flatBytes = Measure(flat);
        var nestedBytes = Measure(nested);

        Assert.True(nestedBytes - flatBytes >= Repetitions * 32,
            $"Flat reduction allocated {flatBytes} bytes and nested reduction allocated {nestedBytes} bytes.");

        static long Measure(TestEvaluation evaluation)
        {
            Assert.True(evaluation.CompleteFileTests([]));
            var passed = true;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < Repetitions; index++)
                passed &= evaluation.CompleteFileTests([]);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(passed);
            return bytes;
        }
    }
}
