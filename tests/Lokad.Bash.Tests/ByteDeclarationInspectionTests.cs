using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteDeclarationInspectionTests
{
    // Bash 5.2.21/UTF-8; only executable/function/line prefixes are omitted from diagnostics.
    [Theory]
    [InlineData("x=$'\\377\\376\\r\\n'; declare -p x; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C3337375C3337365C725C6E270AFFFE0D0A", "")]
    [InlineData("a=([1000000000]=$'\\377' [2]=$'\\376'); declare -p a; printf '%s' \"${a[@]}\"", "6465636C617265202D6120613D285B325D3D24275C33373627205B313030303030303030305D3D24275C33373727290AFEFF", "")]
    [InlineData("declare -A a=(['two words']=$'\\377\\r'); declare -p a; printf '%s' \"${a['two words']}\"", "6465636C617265202D4120613D285B2274776F20776F726473225D3D24275C3337375C722720290AFF0D", "")]
    [InlineData("x=$'\\377'; declare -n ref=x; declare -p ref x; printf '%s' \"$ref\"", "6465636C617265202D6E207265663D2278220A6465636C617265202D2D20783D24275C333737270AFF", "")]
    [InlineData("declare x; y=$'\\377'; z=; declare -p x y z; printf '<%s>' \"${x-unset}\" \"$y\" \"$z\"", "6465636C617265202D2D20780A6465636C617265202D2D20793D24275C333737270A6465636C617265202D2D207A3D22220A3C756E7365743E3CFF3E3C3E", "")]
    [InlineData("x=$'\\376'; declare -p x=$'\\377' x; printf '%s:' \"$?\"; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333736270A313AFE", "6C696E6520313A206465636C6172653A20783DFF3A206E6F7420666F756E640A")]
    [InlineData("x=$'\\376'; declare -p x+=$'\\377' x; printf '%s:' \"$?\"; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333736270A313AFE", "6C696E6520313A206465636C6172653A20782B3DFF3A206E6F7420666F756E640A")]
    [InlineData("x=$'\\376'; declare -p $'\\377' x; printf '%s:' \"$?\"; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333736270A313AFE", "6C696E6520313A206465636C6172653A20FF3A206E6F7420666F756E640A")]
    [InlineData("x=$'\\377'; f(){ local x=$'\\376'; local -p x=$'\\375' x; printf '%s:' \"$?\"; printf '%s' \"$x\"; }; f; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333736270A313AFEFF", "6C696E6520313A206C6F63616C3A20783DFD3A206E6F7420666F756E640A")]
    [InlineData("probe_x=$'\\377'; probe_x=$'\\376' declare -p probe_x; probe_x=$'\\375' declare -p | fixture-bindings; declare -p probe_x", "6465636C617265202D782070726F62655F783D24275C333736270A6465636C617265202D2D2070726F62655F783D24275C333737270A6465636C617265202D2D2070726F62655F783D24275C333737270A", "")]
    [InlineData("probe_x=$'\\377\\r\\n'; declare | fixture-bindings; printf '%s' \"$probe_x\"", "70726F62655F783D24275C3337375C725C6E270AFF0D0A", "")]
    [InlineData("declare -pa a=($'\\377' $'\\376'); printf '%s' \"${a[@]}\"", "6465636C617265202D6120613D285B305D3D24275C33373727205B315D3D24275C33373627290AFFFE", "")]
    [InlineData("x=$'\\377\\r\\n'; snapshot=$(declare -p x); x=changed; eval \"$snapshot\"; printf '%s' \"$x\"", "FF0D0A", "")]
    public async Task InspectionPreservesValueBytesAndBindingState(
        string script, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("x='", "'; declare -p x; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333737270AFF", "")]
    [InlineData("x=ok; declare -p '", "' x; printf '%s' \"$?\"", "6465636C617265202D2D20783D226F6B220A31", "6C696E6520313A206465636C6172653A20FF3A206E6F7420666F756E640A")]
    public async Task InspectionPreservesRawSourceBytes(
        string prefix, string suffix, string outputHex, string errorHex)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(source.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public void RenderingPreservesBytesWithoutChangingTheBinding()
    {
        var variables = Variables.CreateEmpty();
        var value = ShellValue.FromBytes(new byte[] { 0xff });
        Assert.True(variables.TrySetScalar("x", value));
        variables.SetReadonly("x");
        var expected = Utf8Text.Encode("declare -r x=$'\\377'\n");
        var complete = ByteMemoryBuilder.CreateUnaccounted(0);

        Assert.True(variables.TryAppendVariable("x", complete, VariableDisplayStyle.Declaration));
        Assert.Equal(expected.ToArray(), complete.ToMemory().ToArray());
        Assert.True(variables.TryGetScalar("x", out var retained));
        Assert.Equal(value, retained);
        Assert.True(variables.IsReadonly("x"));
    }

    [Fact]
    public void IndexedRenderingReusesScratchAcrossPositions()
    {
        const int count = 512;
        const int outputCapacity = 16 * 1024;
        var variables = Variables.CreateEmpty();
        var values = Enumerable.Repeat(ShellValue.FromText("value"), count).ToArray();
        Assert.True(variables.TryAssignArray("vector", values, ArrayKind.Indexed));
        using var output = ByteMemoryBuilder.CreateUnaccounted(outputCapacity);

        Render();
        AllocationAssert.SustainedReuse(Render, iterations: 12, maximumBytes: 8192);
        Assert.Equal(outputCapacity, output.Capacity);

        void Render()
        {
            output.Truncate(0);
            Assert.True(variables.TryAppendArrayAssignment("vector", output, CancellationToken.None));
        }
    }

    [Fact]
    public void IndexedRenderingDistinguishesHolesUnassignedAndEmptyPositions()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TryAssignArray("vector", [], ArrayKind.Indexed));
        var array = variables.IndexedArrays["vector"];
        Assert.True(IndexedArrayIndex.TryCreate(long.MaxValue, out var last));
        Assert.True(IndexedArrayIndex.TryCreate(17, out var unassigned));
        array.Set(last, ShellValue.FromBytes(new byte[] { 0xff }));
        array.Set(unassigned, null);
        array.Set(default, ShellValue.Empty);
        using var output = ByteMemoryBuilder.CreateUnaccounted(0);

        Assert.True(variables.TryAppendArrayAssignment("vector", output, CancellationToken.None));

        Assert.Equal("=([0]=\"\" [17]= [9223372036854775807]=$'\\377')"u8.ToArray(), output.ToMemory().ToArray());
    }

    [Fact]
    public async Task InspectionRefusesUnadmittedOutputWithoutChangingTheBinding()
    {
        const int InputBytes = 128 * 1024;
        const long MemoryBudgetBytes = 1024 * 1024;
        var input = new byte[InputBytes + 1];
        input.AsSpan().Fill(1); // Display expands each control byte to a four-byte escape.
        input[^1] = (byte)'\n';
        var governor = new MemoryGovernor(MemoryBudgetBytes);
        await using var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.ConfigureHost(host => { host.AddFile("/input", input); }), governor);
        var state = new Eval(Parser.Parse(
            "read -r saved </input; declare -p saved >/dev/null; printf '%s' \"$?\""));

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("1", host.StandardOutput.TextContent);
        Assert.Equal("declare: shared memory budget exhausted\n", host.StandardError.TextContent);
        Assert.True(state.Variables["saved"].Bytes.Span.SequenceEqual(input.AsSpan(0, InputBytes)));
    }

    [Fact]
    public async Task OperandFreeLocalListingWithClosedStdoutStillRefusesUnadmittedOutput()
    {
        const int inputBytes = 128 * 1024;
        var input = new byte[inputBytes + 1];
        input.AsSpan().Fill(1);
        input[^1] = (byte)'\n';
        var governor = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        host.AddFile("/input", input);

        var result = await host.RunAsync(
            "f(){ local saved; read -r saved </input; local -p >&-; printf '%s:%s' \"$?\" \"${#saved}\"; }; f",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"1:{inputBytes}", host.StandardOutput.TextContent);
        Assert.Equal("local: shared memory budget exhausted\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("-a", "i", "4095", "[0]", "[4095]")]
    [InlineData("-A", "key$i", "key4095", "[key0]", "[key4095]")]
    public async Task PrefixedInspectionRendersBorrowedArraysWithinTheOutputAllowance(
        string kind, string selector, string lastIndex, string firstEntry, string lastEntry)
    {
        var governor = new MemoryGovernor(1800 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var script = $"declare {kind} vector\n"
            + $"for ((i=0;i<4096;i++)); do vector[{selector}]=outer; done\n"
            + "f(){ FLAG=temporary declare -p vector; }; f\n"
            + $"printf '%s:%s:%s:%s' \"$?\" \"${{#vector[@]}}\" \"${{vector[{lastIndex}]}}\" \"${{FLAG-missing}}\"";

        var result = await host.RunAsync(script, CancellationToken.None);

        var output = host.StandardOutput.TextContent;
        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith($"declare {kind} vector=({firstEntry}=\"outer\"", output);
        Assert.Contains($"{lastEntry}=\"outer\"", output, StringComparison.Ordinal);
        Assert.EndsWith(")\n0:4096:outer:missing", output);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task InspectionStopsAfterAnOutputFailureWithoutChangingBindings()
    {
        var state = new Eval(Parser.Parse("x=one; y=two; declare -p x y"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("one"), state.Variables["x"]);
        Assert.Equal(ShellValue.FromText("two"), state.Variables["y"]);
        Assert.Collection(host.Appends,
            first => Assert.Equal(FileDescriptor.StdOut, first.Descriptor),
            diagnostic =>
            {
                Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
                Assert.Equal("declare: write error: Write failed\n"u8.ToArray(), diagnostic.Content);
            });
        Assert.Empty(host.ClosedDescriptors);
    }

    [Fact]
    public async Task NamedInspectionDefersTheReachedClosedOutputFailure()
    {
        var state = new Eval(Parser.Parse("x=one; declare -p x missing x 1>&-"));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Output closed\n")));
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(1, state.LastExitCode);
        Assert.Collection(host.Appends,
            row =>
            {
                Assert.Equal(FileDescriptor.Closed, row.Descriptor);
                Assert.Equal("declare -- x=\"one\"\n"u8.ToArray(), row.Content);
            },
            missing =>
            {
                Assert.Equal(FileDescriptor.StdErr, missing.Descriptor);
                Assert.Equal("line 1: declare: missing: not found\n"u8.ToArray(), missing.Content);
            },
            failure =>
            {
                Assert.Equal(FileDescriptor.StdErr, failure.Descriptor);
                Assert.Equal("line 1: declare: write error: Output closed\n"u8.ToArray(), failure.Content);
            });
        Assert.Empty(host.ClosedDescriptors);
    }

    [Theory]
    [InlineData("x=one; y=two; declare -p")]
    [InlineData("x(){ :; }; y(){ :; }; declare -F")]
    public async Task OperandFreeInspectionReportsBadDescriptorFromTheHostAsFailure(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        var host = new TestOperationVisitor([]) { RecordAppends = true, MemoryBudget = owner };
        host.EnqueueAppendResult(DescriptorWriteResult.WriteFailure);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(1, state.LastExitCode);
        Assert.Collection(host.Appends,
            attempted => Assert.Equal(FileDescriptor.StdOut, attempted.Descriptor),
            diagnostic =>
            {
                Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
                Assert.Equal("declare: write error: Bad file descriptor\n"u8.ToArray(), diagnostic.Content);
            });
        Assert.Empty(host.ClosedDescriptors);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DownstreamClosureKeepsTheStatusOfEarlierInspectionOperands(bool functions, bool missing)
    {
        var setup = functions ? "present(){ :; }; later(){ :; }; declare -Fp " : "present=value; later=value; declare -p ";
        var state = new Eval(Parser.Parse(setup + (missing ? "absent present later" : "present later")));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        if (missing)
            host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueAppendResult(DescriptorWriteResult.Closed);

        var result = await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(missing ? 1 : 0, result.ExitCode);
        FileDescriptor[] descriptors = missing ? [FileDescriptor.StdErr, FileDescriptor.StdOut] : [FileDescriptor.StdOut];
        Assert.Equal(descriptors, host.Appends.Select(static append => append.Descriptor));
        if (missing)
            Assert.Equal("line 1: declare: absent: not found\n"u8.ToArray(), host.Appends[0].Content);
        var lastRow = functions ? "declare -f present\n" : "declare -- present=\"value\"\n";
        Assert.Equal(Utf8Text.Encode(lastRow).ToArray(), host.Appends[^1].Content);
    }

    [Fact]
    public async Task OperandFreeInspectionKeepsOneSpellingPerSortedName()
    {
        var names = Enumerable.Range(0, 32)
            .Select(index => "probe_" + new string('x', 118) + $"{index:D4}").ToArray();
        var setup = "export " + string.Join(' ', names.Reverse().Select(name => name + "=value"));
        var state = new Eval(Parser.Parse(setup + "\ndeclare -xp"));
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = governor };
        var rows = new List<(string Text, long Workspace)>();
        host.BeforeAppend = (descriptor, content, _) =>
        {
            if (descriptor == FileDescriptor.StdOut && content.Span.StartsWith("declare -x probe_"u8))
                rows.Add((Utf8Text.Decode(content), state.Memory.Workspace.Bytes));
        };

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(names.Select(name => $"declare -x {name}=\"value\"\n"), rows.Select(row => row.Text));
        Assert.All(rows, row => Assert.Equal(rows[0].Workspace, row.Workspace));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task VariableListingReleasesItsNameSnapshotBeforeWritingFunctions()
    {
        var names = Enumerable.Range(0, 128).Select(index => "probe_" + new string('x', 118) + $"{index:D4}");
        var setup = string.Join(' ', names.Select(name => name + "=value"));
        var state = new Eval(Parser.Parse(setup + "\nshow_only(){ :; }\ndeclare"));
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = governor };
        long variableAllowance = 0, functionAllowance = 0;
        var variableWrites = 0;
        host.BeforeAppend = (descriptor, content, _) =>
        {
            if (descriptor != FileDescriptor.StdOut)
                return;
            if (content.Span.StartsWith("probe_"u8))
            {
                variableAllowance = governor.ReservedBytes;
                variableWrites++;
            }
            else if (content.Span.StartsWith("show_only "u8))
                functionAllowance = governor.ReservedBytes;
        };

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(128, variableWrites);
        Assert.True(functionAllowance > 0);
        Assert.True(variableAllowance - functionAllowance >= 16 * 1024,
            $"Variable write held {variableAllowance} bytes and function write held {functionAllowance} bytes.");
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task PreparedFunctionListingsBorrowNameBytesThroughOutput()
    {
        const int Count = 128;
        var names = Enumerable.Range(0, Count)
            .Select(index => "probe_" + new string('x', 118) + $"{index:D4}").ToArray();
        var setup = string.Join('\n', names.Reverse().Select(name => name + "(){ :; }"))
            + $"\nexport -f {names[0]}\nreadonly -f {names[^1]}\n";
        var all = await RunListingAsync("declare -F");
        var ordinary = string.Concat(names.Skip(1).Take(Count - 2).Select(name => $"declare -f {name}\n"));
        Assert.Equal($"declare -fx {names[0]}\n{ordinary}declare -fr {names[^1]}\n", all);
        var selected = await RunListingAsync("declare -Frx");
        Assert.Equal($"declare -fx {names[0]}\ndeclare -fr {names[^1]}\n", selected);

        async Task<string> RunListingAsync(string listing)
        {
            var state = new Eval(Parser.Parse(setup + listing));
            var governor = new MemoryGovernor(64 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = governor };

            var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

            Assert.Empty(host.StandardError.TextContent);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return host.StandardOutput.TextContent;
        }
    }

    [Fact]
    public async Task ExportListingUsesEachSavedPrefixBindingOnce()
    {
        var result = await EndToEndHarness.RunScriptAsync("""
            export probe_z=right probe_a=original
            probe_a=temp probe_missing=new export -p | fixture-bindings
            declare -p probe_a
            """, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""
            declare -x probe_a="original"
            declare -x probe_z="right"
            declare -x probe_a="original"

            """, result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.TextContent);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Theory]
    [InlineData("declare -p", "line 1: declare: ", ": not found\n", 56 * 1024)]
    [InlineData("declare -fp", "line 1: declare: ", ": not found\n", 32 * 1024)]
    [InlineData("declare", "line 1: declare: `", "': not a valid identifier\n", 56 * 1024)]
    public async Task DeclarationDiagnosticFitsWithoutAnIntermediateOperandCopy(
        string command, string prefix, string suffix, int allowance)
    {
        var operand = new byte[4096];
        operand.AsSpan().Fill((byte)'x');
        operand[^1] = 0xff;
        byte[] expected = [.. Utf8Text.Encode(prefix).Span, .. operand, .. Utf8Text.Encode(suffix).Span];
        var state = new Eval(Parser.Parse(command + " \"$1\" 2>errors"));
        state.ReplacePositionalParameters([ShellValue.FromBytes(operand)]);
        var governor = new MemoryGovernor(allowance);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;

        var descriptor = new FileDescriptor(9);
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
        var writtenDescriptor = write.FileDescriptor;
        var content = write.Content.ToArray();
        write.SetResult(DescriptorWriteResult.Open);
        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, close.Descriptor);
        close.SetResult(null);
        Assert.Null(await state.GetNextStep());
        owner.Dispose();

        Assert.Equal(descriptor, writtenDescriptor);
        Assert.Equal(expected, content);
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(0, governor.ReservedBytes);
    }
}
