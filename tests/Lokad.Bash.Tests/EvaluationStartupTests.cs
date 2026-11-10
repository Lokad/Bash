using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class EvaluationStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectStartupUsesItsOwnStateAndStandardDescriptors(bool nested)
    {
        var directory = new ShellPath("/work");
        var parent = nested
            ? new CommandInvocation("parent", [ShellValue.FromText("argument")], new ShellPath("/parent"),
                [new EnvironmentVariable("PARENT_VALUE", ShellValue.FromText("inherited"))],
                new FileDescriptor(11), new FileDescriptor(12), new FileDescriptor(13))
            : null;
        var evaluation = ShellEvaluationStartup.CreateForNewHost(parent,
            Parser.Parse("inspect \"$#\" \"$PWD\" \"${PARENT_VALUE-unset}\""), new EvaluationOptions(0, directory));
        var host = new MockFileSystem();

        var result = await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(new RunResult(0, directory), result);
        var command = Assert.Single(host.CommandInvocations);
        Assert.Equal(new[] { ShellValue.FromText("0"), ShellValue.FromText("/work"), ShellValue.FromText("unset") },
            command.Arguments);
        Assert.Equal(directory, command.CurrentDirectory);
        Assert.Equal(FileDescriptor.StdIn, command.StdIn);
        Assert.Equal(FileDescriptor.StdOut, command.StdOut);
        Assert.Equal(FileDescriptor.StdErr, command.StdErr);
        Assert.Empty(evaluation.GetDescriptorsForCleanup());
        if (parent is not null)
            Assert.Same(parent.Context.AllocationBudget, evaluation.ExpansionBudget.AllocationBudget);
    }

    [Fact]
    public async Task InitializationCopiesByteArgumentsAndEnvironmentWithoutCommandInvocation()
    {
        var directory = new ShellPath("/work");
        var argumentZero = ShellValue.FromBytes(new byte[] { 0xfe });
        var positional = ShellValue.FromBytes(new byte[] { 0xff });
        var value = ShellValue.FromBytes(new byte[] { 0xa9 });
        var arguments = new List<ShellValue> { positional };
        var environment = new List<EnvironmentVariable>
        {
            new("PWD", ShellValue.FromText("/different")),
            new("VALUE", value)
        };
        var output = new FileDescriptor(11);
        var descriptors = new EvaluationDescriptorSetup(
            DescriptorTable.Snapshot.Standard.WithStandardStreams(FileDescriptor.StdIn, output, FileDescriptor.StdErr),
            [output]);
        var budget = new ExpansionBudget();
        var evaluation = new ShellEvaluation(Parser.Parse("inspect \"$0\" \"$1\" \"$#\" \"$PWD\" \"$VALUE\""),
            new EvaluationOptions(0, directory), descriptors, budget, environment, argumentZero, arguments);

        arguments.Clear();
        environment.Clear();
        var host = new MockFileSystem();
        await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, evaluation.LastExitCode);
        var command = Assert.Single(host.CommandInvocations);
        Assert.Equal(new[] { argumentZero, positional, ShellValue.FromText("1"), ShellValue.FromText("/different"), value },
            command.Arguments);
        Assert.Equal(directory, command.CurrentDirectory);
        Assert.Equal(output, command.StdOut);
        Assert.Empty(command.DescriptorsToClose);
        Assert.Empty(evaluation.GetDescriptorsForCleanup());
        Assert.Same(budget.AllocationBudget, evaluation.ExpansionBudget.AllocationBudget);
        Assert.NotSame(budget, evaluation.ExpansionBudget);
    }
}
