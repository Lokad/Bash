using System.Collections.Generic;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;

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
        var flat = TestEvaluation.Parse(string.Join(" -a ", Enumerable.Repeat("-f file", Leaves)).Split(' '));
        var nested = TestEvaluation.Parse((string.Concat(Enumerable.Repeat("-f file -a ( ", Leaves - 1))
            + "-f file" + string.Concat(Enumerable.Repeat(" )", Leaves - 1))).Split(' '));
        var results = Enumerable.Repeat(true, Leaves).ToArray();

        var flatBytes = Measure(flat);
        var nestedBytes = Measure(nested);

        Assert.True(nestedBytes - flatBytes >= Repetitions * 32,
            $"Flat reduction allocated {flatBytes} bytes and nested reduction allocated {nestedBytes} bytes.");

        long Measure(TestEvaluation evaluation)
        {
            Assert.True(evaluation.CompleteFileTests(results));
            var passed = true;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < Repetitions; index++)
                passed &= evaluation.CompleteFileTests(results);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(passed);
            return bytes;
        }
    }

    [Fact]
    public void TestEvaluation_ConstantChainKeepsBoundedReductionStorage()
    {
        var memory = new EvaluationMemory(new MemoryGovernor(long.MaxValue));
        var shortChain = Arguments(8);
        var longChain = Arguments(1_024);
        var shortBytes = Measure(shortChain);
        var longBytes = Measure(longChain);

        Assert.True(longBytes - shortBytes < 512,
            $"Short chain allocated {shortBytes} bytes and long chain allocated {longBytes} bytes.");

        static ShellValue[] Arguments(int count) => string.Join(" -a ", Enumerable.Repeat("-n yes", count))
            .Split(' ').Select(ShellValue.FromText).ToArray();

        long Measure(ShellValue[] arguments)
        {
            Assert.True(ParseAndComplete());
            var before = GC.GetAllocatedBytesForCurrentThread();
            var matched = ParseAndComplete();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(matched);
            return bytes;

            bool ParseAndComplete() => TestEvaluation.ParseValues(arguments, 0, arguments.Length,
                CancellationToken.None, errexitEnabled: false, memory).CompleteFileTests([]);
        }
    }

    [Fact]
    public void TestEvaluation_AdmitsListsAndValidReductionDuringParsing()
    {
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);

        Assert.False(Parse("-n yes", memory).HasParseError);
        var singleStepBytes = memory.Workspace.Bytes;
        Assert.True(singleStepBytes > 0);
        memory.Workspace.Resize(0);

        Assert.True(Parse("-t 0 -a -n yes extra", memory).HasParseError);
        var listBytes = memory.Workspace.Bytes;
        Assert.Equal(singleStepBytes + MemoryCapacity.ArrayBackingBytes<UnresolvedFileTest>(4), listBytes);
        memory.Workspace.Resize(0);

        Assert.False(Parse("-t 0 -a -n yes", memory).HasParseError);
        Assert.Equal(listBytes + MemoryCapacity.ArrayBackingBytes<bool>(2), memory.Workspace.Bytes);

        VerifyRefusal("-n yes", singleStepBytes - 1);
        VerifyRefusal("-t 0 -a -n yes extra", listBytes - 1);
        VerifyRefusal("-t 0 -a -n yes", listBytes);

        static TestEvaluation Parse(string expression, EvaluationMemory memory)
        {
            var operands = expression.Split(' ').Select(ShellValue.FromText).ToArray();
            return TestEvaluation.ParseValues(operands, 0, operands.Length,
                CancellationToken.None, errexitEnabled: false, memory);
        }

        static void VerifyRefusal(string expression, long limit)
        {
            var budget = new MemoryGovernor(limit);
            using (var scope = budget.CreateScope())
            {
                var memory = new EvaluationMemory(scope);
                // Prepay the limit so the control measures logical usage at the refusal.
                memory.Charge(limit);
                memory.Workspace.Resize(0);
                Assert.Throws<MemoryBudgetExceededException>(() => Parse(expression, memory));
            }
            Assert.Equal(0, budget.ReservedBytes);
        }
    }
}
