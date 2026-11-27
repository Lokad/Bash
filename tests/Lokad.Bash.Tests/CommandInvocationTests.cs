using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;
using static Lokad.Bash.Tests.EndToEnd.EvaluationTestHelper;

namespace Lokad.Bash.Tests;

public sealed class CommandInvocationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownBuiltinWritesItsFullNameWithoutTextAdmission(bool malformed)
    {
        var name = new byte[16 * 1024];
        name.AsSpan().Fill((byte)'x');
        if (malformed)
            name[^1] = 0xff;
        var state = new ShellEvaluation(Parser.Parse("builtin \"$1\""));
        state.ReplacePositionalParameters([ShellValue.FromBytes(name)]);
        var governor = new MemoryGovernor(160 * 1024);
        using var owner = governor.CreateScope();
        var host = new TestOperationVisitor([]) { MemoryBudget = governor, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        var write = Assert.Single(host.Appends);
        Assert.Equal(FileDescriptor.StdErr, write.Descriptor);
        byte[] expected = [.. "line 1: builtin: "u8, .. name, .. ": not a shell builtin\n"u8];
        Assert.Equal(expected, write.Content);
        Assert.Equal(1, result.ExitCode);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("x", 16, true)]
    [InlineData("x", 4096, false)]
    [InlineData("π", 120, false)]
    public async Task CommandMemoryRefusalBoundsTheDiagnosticName(string unit, int count, bool preserveName)
    {
        var name = string.Concat(Enumerable.Repeat(unit, count));
        var state = new ShellEvaluation(Parser.Parse("\"$1\" 2>errors"));
        state.ReplacePositionalParameters([ShellValue.FromText(name)]);
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;

        var descriptor = new FileDescriptor(17);
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var run = Assert.IsType<RunCommand>(await state.GetNextStep());
        Assert.Equal(name, run.Command.CommandName);
        using var other = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
        Assert.True(run.TrySetException(new MemoryBudgetExceededException()));

        var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, write.FileDescriptor);
        Assert.Equal(governor.LimitBytes, governor.ReservedBytes);
        var message = Utf8Text.Decode(write.Content);
        write.SetResult(DescriptorWriteResult.Open);
        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, close.Descriptor);
        close.SetResult(null);
        Assert.Null(await state.GetNextStep());

        Assert.Equal((preserveName ? name : "shell") + ": shared memory budget exhausted\n", message);
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public void RuntimeInheritanceUsesOneUnionAfterSelectingOwnedStreams()
    {
        var output = new FileDescriptor(9);
        var additional = new FileDescriptor(11);
        var bindings = DescriptorBindings.Snapshot.Standard.Fork();
        bindings.Set(new LogicalDescriptor(9), additional);
        var handles = new List<FileDescriptor> { FileDescriptor.StdIn, FileDescriptor.StdErr };
        var collector = new DescriptorHandleCollector(handles);

        var invocation = BashCommandInheritance.CreateInheritedCommand("synthetic", null, [], ShellPath.Root, [],
            FileDescriptor.StdIn, output, FileDescriptor.StdErr, ref collector, bindings.Capture(), [], imports: null);

        Assert.Same(handles, invocation.BorrowedDescriptors);
        Assert.Equal(output, Assert.Single(invocation.DescriptorsToClose));
        Assert.Contains(output, invocation.BorrowedDescriptors);
        Assert.Contains(additional, invocation.BorrowedDescriptors);
    }

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
        var borrowed = new DescriptorHandleCollector(new List<FileDescriptor>());
        var parent = BashCommandInheritance.CreateInheritedCommand("parent", null, [], ShellPath.Root, [],
            FileDescriptor.StdIn, originalOutput, FileDescriptor.StdErr, ref borrowed, CreateBindings(includeAdditionalMappings), functions: [], imports: null);
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
    [InlineData("exec -a -- -l custom", "---", false)]
    [InlineData("exec -a --help custom", "--help", false)]
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
            new CommandContext<BashCommandInheritance>([], [], new BashCommandInheritance(CreateBindings(includeAdditionalMappings), null)));
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
            new CommandContext<BashCommandInheritance>(owned, borrowed, new BashCommandInheritance(mappings.Capture().Bindings, null)))
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
            new CommandContext<BashCommandInheritance>([], [], new BashCommandInheritance(CreateBindings(includeAdditionalMappings), null)))
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

    [Theory]
    [InlineData(false, true, 96, true)]
    [InlineData(true, true, 96, true)]
    [InlineData(false, false, 96, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(false, true, 640, false)]
    [InlineData(true, true, 640, false)]
    public async Task RuntimeEnvironmentAdmitsTheSelectedExportSnapshot(bool query, bool exported, int headroomKiB, bool refuses)
    {
        const int BindingCount = 4096;
        var state = new ShellEvaluation(Parser.Parse(query ? "type -t inspect" : "inspect"));
        var variables = Assert.IsType<Variables>(typeof(ShellEvaluation)
            .GetField("_vars", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(state));
        var value = ShellValue.FromBytes(new byte[] { 0xff });
        for (var index = 0; index < BindingCount; index++)
        {
            BindingName name = $"synthetic_{index:D4}";
            Assert.True(variables.TrySetScalar(name, value));
            if (exported)
                variables.SetExported(name, true);
        }

        var budget = new MemoryGovernor(8 * 1024 * 1024);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;
        // Admit the namespace itself and leave space for command overhead.
        var namespaceBytes = variables.EstimatedMemoryBytes;
        using var hold = budget.Reserve(headroomKiB == 0 ? 0 : budget.LimitBytes - namespaceBytes - headroomKiB * 1024L);
        var step = await state.GetNextStep();
        var workspace = query ? budget.ReservedBytes - namespaceBytes - hold.Bytes
            : state.Memory.Workspace.Bytes - namespaceBytes;
        IReadOnlyList<EnvironmentVariable> environment = [];
        var diagnostic = step as WriteOperation;
        if (diagnostic != null)
            diagnostic.SetResult(DescriptorWriteResult.Open);
        else if (step is RunCommand run)
        {
            environment = run.Command.Environment;
            run.SetResult(new CommandExecutionResult.Completed(0));
        }
        else if (step is ResolveCommandsOperation lookup)
        {
            environment = lookup.Environment;
            lookup.SetResult([new CommandLookupResult.Found("/inspect")]);
            var output = Assert.IsType<WriteOperation>(await state.GetNextStep());
            Assert.Equal("file\n", Encoding.UTF8.GetString(output.Content.Span));
            output.SetResult(DescriptorWriteResult.Open);
        }
        else
            Assert.Fail($"Unexpected operation {step}.");
        Assert.Null(await state.GetNextStep());

        if (refuses)
        {
            Assert.True(environment.Count == 0, $"Published {environment.Count} exports with {workspace} B of command workspace.");
            Assert.Equal(1, state.LastExitCode);
            Assert.NotNull(diagnostic);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Contains("shared memory budget exhausted", Encoding.UTF8.GetString(diagnostic.Content.Span), StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(diagnostic);
            Assert.Equal(exported ? BindingCount + 1 : 1, environment.Count);
            if (exported)
            {
                var snapshot = Assert.IsType<List<EnvironmentVariable>>(environment);
                var payloadBytes = (long)snapshot.Capacity * Unsafe.SizeOf<EnvironmentVariable>();
                Assert.True(workspace >= payloadBytes, $"Snapshot holds {payloadBytes} B with {workspace} B of command workspace.");
                Assert.True(value.Bytes.Equals(Assert.Single(environment, entry => entry.Name == "synthetic_0000").Value.Bytes));
            }
            Assert.Equal(0, state.LastExitCode);
        }
    }

    [Theory]
    [InlineData(false, ArrayKind.Indexed)]
    [InlineData(true, ArrayKind.Indexed)]
    [InlineData(false, ArrayKind.Associative)]
    [InlineData(true, ArrayKind.Associative)]
    public async Task PrefixCommandKeepsExportSnapshotAdmittedWithoutCopyingArray(bool query, ArrayKind kind)
    {
        var state = new ShellEvaluation(Parser.Parse(query ? "FLAG=temporary command -v inspect" : "FLAG=temporary inspect"));
        var variables = Assert.IsType<Variables>(typeof(ShellEvaluation)
            .GetField("_vars", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(state));
        var value = ShellValue.FromBytes(new byte[] { 0xff, 0x20 });
        const int elementCount = 1024;
        var items = kind == ArrayKind.Indexed
            ? Enumerable.Repeat(value, elementCount).ToArray()
            : Enumerable.Range(0, elementCount).SelectMany(index => new[] { ShellValue.FromInteger(index), value }).ToArray();
        Assert.True(variables.TryAssignArray("array", items, kind));
        Assert.Equal(elementCount, kind == ArrayKind.Indexed
            ? variables.IndexedArrays["array"].Count
            : variables.AssociativeArrays["array"].Count);
        variables.SetExported("array", true);
        state.SetExportedVariable("value", value);
        var namespaceBytes = variables.EstimatedMemoryBytes;
        var governor = new MemoryGovernor(namespaceBytes + 96 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var step = await state.GetNextStep(deadline.Token);
        try
        {
            var environment = query
                ? Assert.IsType<ResolveCommandsOperation>(step).Environment
                : Assert.IsType<RunCommand>(step).Command.Environment;
            Assert.Equal(3, environment.Count);
            Assert.DoesNotContain(environment, entry => entry.Name == "array");
            Assert.Equal(value, Assert.Single(environment, entry => entry.Name == "value").Value);
            Assert.Equal(ShellValue.FromText("temporary"), Assert.Single(environment, entry => entry.Name == "FLAG").Value);
            var snapshot = Assert.IsType<List<EnvironmentVariable>>(environment);
            var admitted = query ? governor.ReservedBytes : state.Memory.Workspace.Bytes;
            Assert.True(admitted - namespaceBytes >= (long)snapshot.Capacity * Unsafe.SizeOf<EnvironmentVariable>());
            Assert.False(state.Completion.IsCompleted);
        }
        finally
        {
            if (step is RunCommand command) command.SetResult(new CommandExecutionResult.Completed(0));
            if (step is ResolveCommandsOperation lookup) lookup.SetResult([new CommandLookupResult.Found("/inspect")]);
            if (step is WriteOperation diagnostic) diagnostic.SetResult(DescriptorWriteResult.Open);
        }
        if (query)
        {
            var output = Assert.IsType<WriteOperation>(await state.GetNextStep(deadline.Token));
            Assert.True(output.Content.Span.SequenceEqual("/inspect\n"u8));
            output.SetResult(DescriptorWriteResult.Open);
        }
        Assert.Null(await state.GetNextStep(deadline.Token));
        Assert.Equal(0, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("type -p", 4096, 512, true)]
    [InlineData("command -v", 4096, 512, true)]
    [InlineData("type -p", 4096, 2048, false)]
    [InlineData("command -v", 1, 512, false)]
    public async Task CommandLookupAdmitsItsBatchBeforeCallingTheHost(string query, int count, int limitKiB, bool refuse)
    {
        var script = Parser.Parse(query + " " + string.Join(" ", Enumerable.Repeat("synthetic", count)));
        var state = new ShellEvaluation(script);
        var budget = new MemoryGovernor(limitKiB * 1024L);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;

        var step = await state.GetNextStep();
        var workspace = state.Memory.Workspace.Bytes;
        var reserved = budget.ReservedBytes;
        if (step is ResolveCommandsOperation lookup)
        {
            Assert.Equal(count, lookup.CommandNames.Count);
            Assert.All(lookup.CommandNames, name => Assert.Equal("synthetic", name));
            Assert.Equal("PWD", Assert.Single(lookup.Environment).Name.CachedText);
            var missing = new CommandLookupResult.Missing();
            lookup.SetResult(Enumerable.Repeat<CommandLookupResult>(missing, count).ToArray());
        }
        else
        {
            var diagnostic = Assert.IsType<WriteOperation>(step);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Contains("shared memory budget exhausted", Encoding.UTF8.GetString(diagnostic.Content.Span), StringComparison.Ordinal);
            diagnostic.SetResult(DescriptorWriteResult.Open);
        }
        Assert.Null(await state.GetNextStep());
        Assert.Equal(1, state.LastExitCode);
        Assert.True(refuse == (step is WriteOperation),
            $"Observed {step?.GetType().Name} with {workspace} B logical workspace and {reserved} B root reservation.");
        Assert.InRange(budget.ReservedBytes, 1, 4096);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData("inspect", 4096, 96, true)]
    [InlineData("hash", 4096, 96, true)]
    [InlineData("inspect", 2048, 96, false)]
    [InlineData("inspect", 4096, 256, false)]
    [InlineData("hash", 4096, 256, false)]
    public async Task CommandOperandsAdmitTheirBackingBeforeUse(string command, int count, int limitKiB, bool refuse)
    {
        var state = new ShellEvaluation(Parser.Parse(command + " " + string.Join(" ", Enumerable.Repeat("synthetic", count))));
        var budget = new MemoryGovernor(limitKiB * 1024L);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;

        var step = await state.GetNextStep();
        var workspace = state.Memory.Workspace.Bytes;
        long backingBytes = 0;
        if (step is RunCommand run)
        {
            var arguments = Assert.IsType<List<ShellValue>>(run.Command.Arguments);
            Assert.Equal(count, arguments.Count);
            Assert.All(arguments, argument => Assert.Equal("synthetic", argument.ToUtf8String()));
            backingBytes = (long)arguments.Capacity * Unsafe.SizeOf<ShellValue>();
            run.SetResult(new CommandExecutionResult.Completed(0));
        }
        else if (step is WriteOperation diagnostic)
        {
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Contains("shared memory budget exhausted", Encoding.UTF8.GetString(diagnostic.Content.Span), StringComparison.Ordinal);
            diagnostic.SetResult(DescriptorWriteResult.Open);
        }
        if (step != null)
            Assert.Null(await state.GetNextStep());

        Assert.True(refuse == (step is WriteOperation),
            $"Observed {step?.GetType().Name ?? "completion"} with {workspace} B of workspace and {backingBytes} B of operand backing.");
        if (refuse || command == "hash")
            Assert.Equal(1, state.LastExitCode);
        else
        {
            Assert.IsType<RunCommand>(step);
            Assert.Equal(0, state.LastExitCode);
            Assert.True(workspace >= backingBytes, $"Operand backing held {backingBytes} B with only {workspace} B of workspace.");
        }
        Assert.InRange(budget.ReservedBytes, 1, 4096);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task InheritedEvaluationsKeepCapturedMappingsPrivate()
    {
        var mappings = new DescriptorTable();
        mappings.Set(new LogicalDescriptor(9), new FileDescriptor(11));
        var borrowed = new DescriptorHandleCollector(new List<FileDescriptor> { new FileDescriptor(11) });
        var invocation = BashCommandInheritance.CreateInheritedCommand("sh", null, [], ShellPath.Root, [],
            FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr, ref borrowed,
            mappings.Capture().Bindings, functions: [], imports: null);

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
