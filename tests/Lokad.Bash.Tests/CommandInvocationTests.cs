using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;
using static Lokad.Bash.Tests.EndToEnd.EvaluationTestHelper;

namespace Lokad.Bash.Tests;

public sealed class CommandInvocationTests
{
    private readonly record struct TestInheritance(int Value);

    [Fact]
    public void TypedInheritanceSurvivesAliasesAndChildren()
    {
        var inheritance = new TestInheritance(42);
        var parent = CommandInvocation.CreateWithInheritance("parent", [], ShellPath.Root, [],
            FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr, [], [], inheritance, argumentZero: null, functions: []);
        var alias = parent.WithCommandName("alias");
        var child = alias.CreateChild("child", [], [], FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);

        foreach (var command in new[] { parent, alias, child })
        {
            Assert.True(command.TryGetInheritance<TestInheritance>(out var inherited));
            Assert.Equal(inheritance, inherited);
            Assert.False(command.TryGetInheritance<Guid>(out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BashInheritanceRebindsStreamsAcrossMultipleChildren(bool includeAdditionalMappings)
    {
        var originalOutput = new FileDescriptor(9);
        var intermediateOutput = new FileDescriptor(12);
        var finalOutput = new FileDescriptor(13);
        var parent = BashCommandInheritance.CreateInheritedCommand("parent", null, [], ShellPath.Root, [],
            FileDescriptor.StdIn, originalOutput, FileDescriptor.StdErr, [], CreateBindings(includeAdditionalMappings), functions: []);
        var child = parent.WithCommandName("alias").CreateChild("wrapper", [], [],
            FileDescriptor.StdIn, intermediateOutput, FileDescriptor.StdErr);
        var grandchild = child.CreateChild("sh", [], [], originalOutput, finalOutput, FileDescriptor.StdErr);

        Assert.Equal(finalOutput, Assert.Single(grandchild.DescriptorsToClose));
        Assert.Contains(intermediateOutput, grandchild.BorrowedDescriptors);
        Assert.Equal(originalOutput, await ReadTerminalTestDescriptorAsync(
            ShellEvaluationStartup.CreateInherited(grandchild, Parser.Parse("test -t 0"), ShellValue.FromText("sh"), [])));
        Assert.Equal(finalOutput, await ReadTerminalTestDescriptorAsync(
            ShellEvaluationStartup.CreateInherited(grandchild, Parser.Parse("test -t 1"), ShellValue.FromText("sh"), [])));
        Assert.Equal(includeAdditionalMappings ? new FileDescriptor(11) : FileDescriptor.Closed,
            await ReadTerminalTestDescriptorAsync(
                ShellEvaluationStartup.CreateInherited(grandchild, Parser.Parse("test -t 9"), ShellValue.FromText("sh"), [])));

    }

    [Fact]
    public void RenamingKeepsArgumentZeroButNewChildrenUseTheirOwnDefault()
    {
        var name = ShellValue.FromBytes(new byte[] { 0xff });
        var invocation = new CommandInvocation("sh", [], ShellPath.Root, [], null, null, null)
        { ArgumentZeroOverride = name };

        Assert.Equal(name, invocation.WithCommandName("/usr/bin/sh").ArgumentZeroOverride);
        Assert.Null(invocation.CreateChild("sh", [], [], FileDescriptor.StdIn, FileDescriptor.StdOut,
            FileDescriptor.StdErr).ArgumentZeroOverride);
    }

    [Theory]
    [InlineData("exec -a renamed custom", "renamed", false)]
    [InlineData("exec -c custom", "custom", true)]
    [InlineData("exec -cl -a alias custom", "-alias", true)]
    [InlineData("exec -a first -a '' -l custom", "-", false)]
    [InlineData("command -p exec -ca name custom", "name", true)]
    public async Task ExecSeparatesLookupNameFromArgumentZeroAndEnvironment(
        string command, string expectedName, bool clearEnvironment)
    {
        var directory = new ShellPath("/work");
        var evaluation = new ShellEvaluation(Parser.Parse(
            "PWD=/different PATH=/caller/path MARKER=prefix " + command + " argument; unexpected"),
            options: new EvaluationOptions(0, directory));
        var host = new MockFileSystem();

        await new Engine().RunAsync(evaluation, host);

        var invocation = Assert.Single(host.CommandInvocations);
        Assert.Equal("custom", invocation.CommandName);
        Assert.Equal(ShellValue.FromText(expectedName), invocation.ArgumentZeroOverride);
        Assert.Equal(ShellValue.FromText("argument"), Assert.Single(invocation.Arguments));
        Assert.Equal(directory, invocation.CurrentDirectory);
        if (clearEnvironment)
            Assert.Empty(invocation.Environment);
        else
            Assert.Equal(ShellValue.FromText("prefix"), Assert.Single(invocation.Environment, item => item.Name == "MARKER").Value);
        Assert.Equal(0, evaluation.LastExitCode);
    }

    [Fact]
    public void IndependentCommandsAndChildrenHaveNoBashInheritance()
    {
        var parent = new CommandInvocation("parent", [], ShellPath.Root, [], null, null, null);
        var alias = parent.WithCommandName("alias");
        var child = alias.CreateChild("child", [], [], FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);

        Assert.IsType<CommandContext>(parent.Context);
        Assert.IsType<CommandContext>(child.Context);
        Assert.False(parent.TryGetInheritance<BashCommandInheritance>(out _));
        Assert.False(child.TryGetInheritance<BashCommandInheritance>(out _));
        Assert.Same(parent.Context, alias.Context);
    }

    private static DescriptorBindings.Snapshot CreateBindings(bool includeAdditionalMappings)
    {
        var table = new DescriptorTable();
        if (includeAdditionalMappings)
            table.Set(new LogicalDescriptor(9), new FileDescriptor(11));
        return table.Capture().Bindings;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildInvocationsPreserveTheDirectoryWhenTheirEnvironmentChanges(bool includeAdditionalMappings)
    {
        var directory = new ShellPath("/work");
        var parent = new CommandInvocation("parent", [], directory, [], null, null, null,
            new CommandContext<BashCommandInheritance>([], [], new BashCommandInheritance(CreateBindings(includeAdditionalMappings))));
        var environment = new[] { new EnvironmentVariable("PWD", ShellValue.FromText("/other")) };
        var child = parent.CreateChild("child", [], environment,
            FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);

        Assert.Same(directory, child.CurrentDirectory);
        Assert.Same(directory, child.WithCommandName("renamed").CurrentDirectory);
        Assert.Same(environment, child.Environment);
    }

    [Fact]
    public void TextEnvironmentRejectsByteNamesAndReusesTextNames()
    {
        var invocation = new CommandInvocation("inspect", [], currentDirectory: ShellPath.Root, [], null, null, null);
        var name = new string('v', 100);
        Assert.True(invocation.TryDecodeEnvironmentVariable(new(name, ShellValue.FromText("value")),
            out var decodedName, out var value, out var error));
        Assert.Same(name, decodedName);
        Assert.Equal("value", value);
        Assert.True(error.IsEmpty);

        var byteName = BindingName.FromValue(ShellValue.FromBytes(new byte[] { (byte)'a', (byte)'[', 0xff, (byte)']' }));
        Assert.False(invocation.TryDecodeEnvironmentVariable(new(byteName, ShellValue.FromText("value")),
            out _, out _, out error));
        Assert.Equal("shell: text argument must be valid UTF-8\n", ShellValue.FromBytes(error).ToUtf8String());
    }

    [Fact]
    public async Task RenamingPreservesOwnershipAndInheritedContext()
    {
        var mappings = new DescriptorTable();
        mappings.Set(new LogicalDescriptor(9), new FileDescriptor(11));
        var borrowed = new FileDescriptor[] { new(9) };
        var owned = new FileDescriptor[] { new(10) };
        var invocation = new CommandInvocation("original", [], currentDirectory: ShellPath.Root, [],
            borrowed[0], owned[0], null,
            new CommandContext<BashCommandInheritance>(owned, borrowed, new BashCommandInheritance(mappings.Capture().Bindings)))
        {
            Functions = [new("f(){ :; }"u8.ToArray())]
        };

        var renamed = invocation.WithCommandName("normalized");

        Assert.Equal("normalized", renamed.CommandName);
        Assert.Same(owned, renamed.DescriptorsToClose);
        Assert.Same(invocation.Functions, renamed.Functions);
        Assert.Equal(new FileDescriptor(11), await ReadTerminalTestDescriptorAsync(
            ShellEvaluationStartup.CreateInherited(renamed, Parser.Parse("test -t 9"), ShellValue.FromText("sh"), [])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildCommandsRetainExportedFunctions(bool includeAdditionalMappings)
    {
        var parent = new CommandInvocation(
            "parent", [], currentDirectory: ShellPath.Root, [], null, null, null,
            new CommandContext<BashCommandInheritance>([], [], new BashCommandInheritance(CreateBindings(includeAdditionalMappings))))
        {
            Functions = [new("f(){ :; }"u8.ToArray())]
        };

        var child = parent.CreateChild("child", [], [],
            FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr);

        Assert.Same(parent.Functions, child.Functions);
    }

    [Fact]
    public void Constructor_DefaultStreamsAreNotOwned()
    {
        var invocation = new CommandInvocation("inspect", [], currentDirectory: ShellPath.Root, [], null, null, null);

        Assert.Equal(FileDescriptor.StdIn, invocation.StdIn);
        Assert.Equal(FileDescriptor.StdOut, invocation.StdOut);
        Assert.Equal(FileDescriptor.StdErr, invocation.StdErr);
        Assert.Empty(invocation.DescriptorsToClose);
    }

    [Fact]
    public void Constructor_ReservedStreamsAreNotOwned()
    {
        var invocation = new CommandInvocation(
            "inspect", [], currentDirectory: ShellPath.Root, [], FileDescriptor.Closed, FileDescriptor.DevNull, FileDescriptor.StdErr);

        Assert.Equal(FileDescriptor.Closed, invocation.StdIn);
        Assert.Equal(FileDescriptor.DevNull, invocation.StdOut);
        Assert.Equal(FileDescriptor.StdErr, invocation.StdErr);
        Assert.Empty(invocation.DescriptorsToClose);
    }

    [Fact]
    public void Constructor_DeduplicatesOwnedStreamsInEncounterOrder()
    {
        var input = new FileDescriptor(9);
        var output = new FileDescriptor(7);
        var invocation = new CommandInvocation("inspect", [], currentDirectory: ShellPath.Root, [], input, output, input);

        Assert.Equal(new[] { input, output }, invocation.DescriptorsToClose);
    }

    [Fact]
    public async Task ChildCommandsOwnNewStreamsWhileTheirHostedEvaluationBorrowsThem()
    {
        var parentOutput = new FileDescriptor(9);
        var childOutput = new FileDescriptor(11);
        var parent = new CommandInvocation("parent", [], ShellPath.Root, [], null, parentOutput, null);
        var child = parent.CreateChild("sh", [], [], parentOutput, childOutput, childOutput);

        Assert.Equal(parentOutput, Assert.Single(parent.DescriptorsToClose));
        Assert.Equal(childOutput, Assert.Single(child.DescriptorsToClose));
        Assert.Equal(parentOutput, await ReadTerminalTestDescriptorAsync(
            ShellEvaluationStartup.CreateInherited(child, Parser.Parse("exec 1>&-; test -t 0"), ShellValue.FromText("sh"), [])));
        Assert.Equal(childOutput, await ReadTerminalTestDescriptorAsync(
            ShellEvaluationStartup.CreateInherited(child, Parser.Parse("test -t 1"), ShellValue.FromText("sh"), [])));
    }

    [Fact]
    public void BorrowedDescriptors_ExcludeEveryAliasWithoutAddingOtherHandles()
    {
        var borrowed = new FileDescriptor(9);
        var owned = new FileDescriptor(7);
        var unrelated = new FileDescriptor(15);
        var invocation = CommandInvocation.CreateWithBorrowedDescriptors(
            "inspect", [], currentDirectory: ShellPath.Root, [], borrowed, owned, borrowed, [borrowed, unrelated]);

        Assert.Equal(borrowed, invocation.StdIn);
        Assert.Equal(owned, invocation.StdOut);
        Assert.Equal(borrowed, invocation.StdErr);
        Assert.Equal(owned, Assert.Single(invocation.DescriptorsToClose));
    }

    [Fact]
    public void BorrowedDescriptors_AllBorrowedStreamsRemainCallerOwned()
    {
        var input = new FileDescriptor(9);
        var output = new FileDescriptor(7);
        var invocation = CommandInvocation.CreateWithBorrowedDescriptors(
            "inspect", [], currentDirectory: ShellPath.Root, [], input, output, input, [input, output]);

        Assert.Empty(invocation.DescriptorsToClose);
    }

    [Fact]
    public void ExplicitOwnership_RetainsCompletedListsAndInvocationContext()
    {
        var arguments = new List<ShellValue> { ShellValue.FromText("argument") };
        var environment = new List<EnvironmentVariable> { new("PWD", ShellValue.FromText("/work")) };
        var stream = new FileDescriptor(9);
        var ownership = new List<FileDescriptor> { new(15) };
        var invocation = new CommandInvocation(
            "inspect", arguments, currentDirectory: new ShellPath("/work"), environment, stream, stream, stream,
            new CommandContext(ownership, []));

        Assert.Equal("inspect", invocation.CommandName);
        Assert.Same(arguments, invocation.Arguments);
        Assert.Same(environment, invocation.Environment);
        Assert.Equal(new ShellPath("/work"), invocation.CurrentDirectory);
        Assert.Equal(stream, invocation.StdIn);
        Assert.Equal(stream, invocation.StdOut);
        Assert.Equal(stream, invocation.StdErr);
        Assert.Same(ownership, invocation.DescriptorsToClose);
    }

    [Fact]
    public async Task InheritedEvaluationsInitializeByteArguments()
    {
        var invocation = new CommandInvocation("sh", [], ShellPath.Root, [], null, null, null);
        var argumentZero = ShellValue.FromBytes(new byte[] { 0xfe });
        var arguments = new[] { ShellValue.FromBytes(new byte[] { 0xff }), ShellValue.FromText("a b") };
        var evaluation = ShellEvaluationStartup.CreateInherited(
            invocation, Parser.Parse("inspect \"$0\" \"$1\" \"$2\" \"$#\""), argumentZero, arguments);
        var host = new MockFileSystem();

        await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(new[] { argumentZero, arguments[0], arguments[1], ShellValue.FromText("2") },
            Assert.Single(host.CommandInvocations).Arguments);
    }

    [Fact]
    public async Task InheritedEvaluationsKeepCapturedMappingsPrivate()
    {
        var mappings = new DescriptorTable();
        mappings.Set(new LogicalDescriptor(9), new FileDescriptor(11));
        var invocation = BashCommandInheritance.CreateInheritedCommand("sh", null, [], ShellPath.Root, [],
            FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr, [new FileDescriptor(11)],
            mappings.Capture().Bindings, functions: []);

        mappings.Set(new LogicalDescriptor(9), new FileDescriptor(12));

        Assert.Equal(FileDescriptor.StdOut, await ReadTerminalTestDescriptorAsync(
            ShellEvaluationStartup.CreateInherited(invocation, Parser.Parse("exec 9>&1; test -t 9"), ShellValue.FromText("sh"), [])));
        Assert.Equal(new FileDescriptor(11), await ReadTerminalTestDescriptorAsync(
            ShellEvaluationStartup.CreateInherited(invocation, Parser.Parse("test -t 9"), ShellValue.FromText("sh"), [])));
    }

    [Fact]
    public async Task NewHostEvaluationUsesStandardDescriptorsAndIndependentVariables()
    {
        var options = new EvaluationOptions(0, new ShellPath("/child"));
        var evaluation = ShellEvaluationStartup.CreateForNewHost(Parser.Parse("test -t 9"), options);

        Assert.Equal(options.CurrentDirectory, evaluation.CurrentDirectory);
        Assert.False(evaluation.Variables.ContainsKey("PARENT"));
        Assert.Equal(FileDescriptor.StdIn, await ReadTerminalTestDescriptorAsync(
            ShellEvaluationStartup.CreateForNewHost(Parser.Parse("test -t 0"), options)));
        Assert.Equal(FileDescriptor.StdOut, evaluation.GetStdOutDescriptor());
        Assert.Equal(FileDescriptor.StdErr, evaluation.GetStdErrDescriptor());
        Assert.Equal(FileDescriptor.Closed, await ReadTerminalTestDescriptorAsync(evaluation));
        Assert.Empty(evaluation.GetDescriptorsForCleanup());
    }


}
