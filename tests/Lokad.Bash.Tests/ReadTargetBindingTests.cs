using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ReadTargetBindingTests
{
    [Theory]
    [InlineData("", "  one\\ two  \nnext\n", "<0><  one two  ><next>")]
    [InlineData("-d :", " a\\:b  :next\n", "<0>< a:b  ><next>")]
    [InlineData("-n 4", " a\\ bnext\n", "<0>< a b><next>")]
    [InlineData("", "  a\\ b  ", "<1><  a b  ><>")]
    public async Task ReadDecodesTheWholeImplicitReplyAcrossRecordRoutes(string options, string input, string expected)
    {
        var script = $$"""
            separator=' :'
            unset IFS
            declare -n IFS=separator
            read {{options}}
            status=$?
            read -r tail
            printf '<%s><%s><%s>' "$status" "$REPLY" "$tail"
            """;
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(script, host, System.Text.Encoding.UTF8.GetBytes(input));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("", "a\\ b c\nnext word\n", "<0:a b:c><0:next:word>")]
    [InlineData("-d :", "a\\ b c:next word:", "<0:a b:c><0:next:word>")]
    [InlineData("-n 5", "a\\ b cnext ", "<0:a b:c><0:next:>")]
    public async Task ReadInterpretsRetainedEscapesAcrossRecordRoutes(string options, string input, string expected)
    {
        var script = $$"""
            IFS=' '
            read {{options}} first last
            printf '<%s:%s:%s>' "$?" "$first" "$last"
            read {{options}} first last
            printf '<%s:%s:%s>' "$?" "$first" "$last"
            """;
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(script, host, System.Text.Encoding.UTF8.GetBytes(input));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("a=([1]=old [4]=seed); declare -n REPLY='a[4]'; builtin read -r; status=$?",
        "${a[1]}", "${a[4]}")]
    [InlineData("declare -A a=([keep]=old [key]=seed); f(){ local -n REPLY='a[key]'; command read -r; }; f; status=$?",
        "${a[keep]}", "${a[key]}")]
    public async Task ReadImplicitReplyResolvesAnArrayElementAndPreservesWhitespace(
        string read, string untouched, string destination)
    {
        var script = $$"""
            {{read}}
            IFS= read -r next
            printf '<%s>' "$status" "{{untouched}}" "{{destination}}" "$next"
            """;
        var host = EndToEndHarness.BuildTree([]);
        var result = await EndToEndHarness.RunScriptAsync(script, host, "  one\\two  \nnext\n"u8.ToArray());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0><old><  one\\two  ><next>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("""a=([2]=old [9]=keep); REPLY=reply; read -a a <<< 'one two three'; printf '<%s><%s>' "${#a[@]}" "$REPLY"; printf '[%s]' "${a[@]}" """,
        "<3><reply>[one][two][three]", "")]
    [InlineData("""a=(old); read -a a </dev/null; printf '<%s><%s>' "$?" "${#a[@]}" """,
        "<1><0>", "")]
    [InlineData("""a=old; read -a a <input; printf '<%s><%s>' "$?" "${#a[@]}"; printf '[%s]' "${a[@]}" """,
        "<1><3>[one][two][three]", "")]
    [InlineData("""a=(old); { read -a 'a[i++]'; printf '<%s>' "$?"; read tail; }; printf '<%s><%s><%s>' "${a[0]}" "${i-0}" "$tail" """,
        "<1><old><0><two>", "line 1: read: `a[i++]': not a valid identifier\n")]
    [InlineData("""declare -A a=([key]=keep); declare -n ref=a; { read -a ref; printf '<%s>' "$?"; read tail; }; printf '<%s><%s>' "${a[key]}" "$tail" """,
        "<1><keep><two>", "line 1: read: ref: not an indexed array\n")]
    [InlineData("""a=(old); readonly a; declare -n ref=a; { read -a ref; printf '<%s>' "$?"; read tail; }; printf '<%s><%s>' "${a[0]}" "$tail" """,
        "<1><old><two>", "line 1: ref: readonly variable\n")]
    [InlineData("""a=(outer keep); f(){ local -n ref=a; a=temp read -a ref <<< 'one two'; }; f; printf '<%s>' "${a[@]}" """,
        "<outer><keep>", "")]
    [InlineData("""a=(old); declare -rn ref=a; read -ra ref <<< 'one two'; printf '<%s>' "${a[@]}" """,
        "<one><two>", "")]
    [InlineData("""declare -n a; read -a a 2>&- <<< 'one two'; printf '<%s>' "${a[@]}" """,
        "<one><two>", "")]
    public async Task ReadArraySelectsTheWholeTargetAfterConsumingInput(string script, string output, string error)
    {
        var host = EndToEndHarness.BuildTree([("/input", "one two three")]);
        var result = await EndToEndHarness.RunScriptAsync(script, host, "one\ntwo\n"u8.ToArray());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("-raarr -- -r", 1, "-r", "old", "one two")]
    [InlineData("-a arr ''", 1, "", "old", "one two")]
    [InlineData("-a 1bad 2bad", 1, "2bad", "old", "one two")]
    [InlineData("-a 1bad ignored", 1, "1bad", "old", "next")]
    [InlineData("-a arr 'unused[i++]' 1bad", 0, null, "one,two", "next")]
    public async Task ReadArrayValidatesFirstPositionalNameBeforeReading(
        string options, int status, string? invalidName, string array, string remainder)
    {
        var script = "arr=(old); i=0; read " + options
            + """
            ; status=$?; IFS= read -r next; IFS=,; printf '<%s><%s><%s><%s>' "$status" "${arr[*]}" "$i" "$next"
            """;
        var host = EndToEndHarness.BuildTree([]);
        var result = await EndToEndHarness.RunScriptAsync(script, host, "one two\nnext\n"u8.ToArray());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<{status}><{array}><0><{remainder}>", result.StdOut);
        Assert.Equal(invalidName is null ? "" : $"line 1: read: `{invalidName}': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("""IFS=: read -a a <<< ':one::two:'; printf '[%s]' "${a[@]}" """, "[][one][][two]")]
    [InlineData("""IFS=: read -a a <<< 'a\:b::c'; printf '[%s]' "${a[@]}" """, "[a:b][][c]")]
    [InlineData("""IFS=: read -ra a <<< 'a\:b::c'; printf '[%s]' "${a[@]}" """, "[a\\][b][][c]")]
    [InlineData("""IFS= read -ra a <<< ' one two '; printf '[%s]' "${a[@]}" """, "[ one two ]")]
    [InlineData("""IFS=é read -ra a <<< 'oneétwoéthree'; printf '[%s]' "${a[@]}" """, "[one][two][three]")]
    [InlineData("""read -ra a ignored '1bad' -z <<< 'one two'; printf '[%s]' "${a[@]}" """, "[one][two]")]
    public async Task ReadArraySplitsEveryFieldWithoutFoldingTheRemainder(string script, string output)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("IFS=' ' read value", "head tail\\\\\\  ", "<unset><head tail\\><next>")]
    [InlineData("IFS=' ' read value", "head tail\\\\\\\\  ", "<unset><head tail\\\\><next>")]
    [InlineData("IFS=' ' read value", " one\\  ", "<unset><one ><next>")]
    [InlineData("IFS=' :' read -r value", "head tail\\  ", "<unset><head tail\\><next>")]
    [InlineData("IFS=$' \\t:' read left value", "first : next last\\\t \t", "<first><next last><next>")]
    public async Task ReadScalarRemainderTrimsOnlyTrailingIfsWhitespace(string read, string record, string output)
    {
        var script = read + "\n" + """
            IFS= read -r next
            printf '<%s>' "${left-unset}" "$value" "$next"
            """;
        var host = EndToEndHarness.BuildTree([]);
        var result = await EndToEndHarness.RunScriptAsync(script, host, System.Text.Encoding.UTF8.GetBytes(record + "\nnext\n"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ReadArrayPreservesBytesAndLeavesTheNextDelimitedRecord()
    {
        var host = EndToEndHarness.BuildTree([]);
        var result = await EndToEndHarness.RunScriptAsync(
            """read -a a -rd:; read -rd: tail; printf '<%s><%s><%s>' "${a[0]}" "${a[1]}" "$tail" """,
            host, Convert.FromHexString("FF20FE0D3A6E6578743A"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString("3CFF3E3CFE0D3E3C6E6578743E"), host.StandardOutput.Content.ToArray());
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadRetainsDecodedBytesAndDetachesBorrowedRecordFields(bool array)
    {
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = governor };
        host.SetStandardInputBytes(Convert.FromHexString("5CFF5CFE207261770A6E657874206C696E650A"));
        var state = new Eval(Parser.Parse(array ? "read -a values; read tail" : "read decoded raw; read tail"));

        await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        var decoded = array ? state.IndexedArrays["values"][0] : state.Variables["decoded"];
        var raw = array ? state.IndexedArrays["values"][1] : state.Variables["raw"];
        Assert.True(decoded.Bytes.Span.SequenceEqual(new byte[] { 0xff, 0xfe }));
        Assert.True(decoded.TryGetCompactArrayCapacity(out var capacity));
        Assert.Equal(4, capacity);
        Assert.Equal(ShellValue.FromText("raw"), raw);
        Assert.Equal(ShellValue.FromText("next line"), state.Variables["tail"]);
        Assert.Equal(0, state.LastExitCode);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadRefundsDecodedFieldsBeforeTheNextCommand()
    {
        var value = new string('x', 8191);
        var expected = ShellValue.FromText(value);

        // Both inputs need the reusable normalization buffer. Only the escaped field needs decoding scratch.
        var plain = await MeasureAsync(value + "\0\nnext\n");
        var escaped = await MeasureAsync("\\" + value + "\nnext\n");

        Assert.Equal(plain, escaped);

        async Task<long> MeasureAsync(string input)
        {
            var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
            using var owner = governor.CreateScope();
            await using var host = new MockFileSystem { MemoryBudget = governor };
            host.SetStandardInput(input);
            var state = new Eval(Parser.Parse("read value; printf '%s' \"$?\"; read tail"));
            state.Memory.Budget = owner;

            var read = Assert.IsType<ReadLineOperation>(await state.GetNextStep());
            await ShellOperationDispatcher.DispatchAsync(read, host, CancellationToken.None);
            var marker = Assert.IsType<WriteOperation>(await state.GetNextStep());
            Assert.Equal(FileDescriptor.StdOut, marker.FileDescriptor);
            Assert.True(marker.Content.Span.SequenceEqual("0"u8));
            Assert.Equal(expected, state.Variables["value"]);
            var admitted = governor.ReservedBytes;
            marker.SetResult(DescriptorWriteResult.Open);

            var next = Assert.IsType<ReadLineOperation>(await state.GetNextStep());
            Assert.NotNull(read.ReusableLine);
            Assert.Same(read.ReusableLine, next.ReusableLine);
            await ShellOperationDispatcher.DispatchAsync(next, host, CancellationToken.None);
            Assert.Null(await state.GetNextStep());
            Assert.Equal(expected, state.Variables["value"]);
            Assert.Equal(ShellValue.FromText("next"), state.Variables["tail"]);
            return admitted;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadDelimitedKeepsTheBufferOwnerThroughThePendingBorrow(bool cancel)
    {
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        await using var host = new PendingReadHost { MemoryBudget = governor };
        host.SetStandardInput("value:");
        var state = new Eval(Parser.Parse("value=old; read -d : value; printf '%s' \"$value\""));
        state.Memory.Budget = owner;
        var evaluation = state.EnsureRunning(cancellation.Token);
        var read = Assert.IsType<ReadInputOperation>(await state.GetNextStep());
        var before = governor.ReservedBytes;
        var dispatch = ShellOperationDispatcher.DispatchAsync(read, host, cancellation.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var whilePending = governor.ReservedBytes;
        if (cancel)
            cancellation.Cancel();
        var endedBeforeHost = evaluation.IsCompleted;
        host.Release.SetResult();

        Assert.Equal(before, whilePending);
        Assert.False(endedBeforeHost);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.GetNextStep());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
            Assert.Equal(ShellValue.FromText("old"), state.Variables["value"]);
            Assert.Equal(0, host.ReadBytesConsumed);
        }
        else
        {
            await dispatch;
            var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
            Assert.Equal(FileDescriptor.StdOut, write.FileDescriptor);
            Assert.True(write.Content.Span.SequenceEqual("value"u8));
            Assert.Equal(6, host.ReadBytesConsumed);
            write.SetResult(DescriptorWriteResult.Open);
            Assert.Null(await state.GetNextStep());
            await evaluation;
        }
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadDelimitedPublishesAShortRecordWithinTheBudget()
    {
        var governor = new MemoryGovernor(64 * 1024);
        await using var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), governor);
        host.SetStandardInput("a\\:b:tail\n");
        using var owner = governor.CreateScope();
        var state = new Eval(Parser.Parse("IFS= read -d: value; status=$?; IFS= read -r tail"));

        await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(ShellValue.FromText("a:b"), state.Variables["value"]);
        Assert.Equal(ShellValue.FromText("tail"), state.Variables["tail"]);
        Assert.Equal(ShellValue.FromText("0"), state.Variables["status"]);
        Assert.Empty(host.StandardError.Content.ToArray());
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadDelimitedCarriesAnEscapeIntoTheNextWindow()
    {
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        await using var host = new MockFileSystem { MemoryBudget = governor, MaximumReadWindowBytes = 2 };
        host.SetStandardInput("a\\:b:tail\n");
        using var owner = governor.CreateScope();
        var state = new Eval(Parser.Parse("IFS= read -d: value; status=$?; IFS= read -r tail"));

        await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(ShellValue.FromText("a:b"), state.Variables["value"]);
        Assert.Equal(ShellValue.FromText("tail"), state.Variables["tail"]);
        Assert.Equal(ShellValue.FromText("0"), state.Variables["status"]);
        Assert.Equal(10, host.ReadBytesConsumed);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadDelimitedFailureConsumesOnlyTheFailedPhysicalRecord()
    {
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using var owner = governor.CreateScope();
        var bytes = "a\\:tail"u8.ToArray();
        var error = new OperationError("fixture read failure\n");
        var borrow = new DescriptorReadBufferOwner(static _ => { }, owner);
        var host = new TestOperationVisitor([])
        {
            ReadOverride = _ => ValueTask.FromResult(borrow.Borrow(bytes,
                ByteReadResult.Failure(bytes.Length, error, false), canRetain: true))
        };
        var state = new Eval(Parser.Parse("value=old; read -d: value"));
        state.Memory.Budget = owner;
        var read = await state.GetNextStep();
        Assert.NotNull(read);
        await ShellOperationDispatcher.DispatchAsync(read, host, CancellationToken.None);
        var diagnostic = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.True(diagnostic.Content.Span.SequenceEqual("line 1: read: read error: 0: fixture read failure\n"u8));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await state.GetNextStep());
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), state.Variables["value"]);
        Assert.Equal(3, borrow.Position);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadArrayKeepsItsTargetOnPublicationRefusal()
    {
        var governor = new MemoryGovernor(96 * 1024);
        await using var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), governor);
        host.SetStandardInputBytes(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("x ", 1000)) + "\n"));
        using var owner = governor.CreateScope();
        var state = new Eval(Parser.Parse("a=(old keep); read -a a; status=$?"));

        await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(new[] { ShellValue.FromText("old"), ShellValue.FromText("keep") }, state.IndexedArrays["a"]);
        Assert.Equal(ShellValue.FromText("1"), state.Variables["status"]);
        Assert.Equal("read: shared memory budget exhausted\n", host.StandardError.TextContent);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("-rN81920")]
    [InlineData("-rd:")]
    public async Task ReadKeepsItsTargetOnInputBufferRefusal(string options)
    {
        var governor = new MemoryGovernor(96 * 1024);
        await using var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), governor);
        host.SetStandardInput(new string('x', 80 * 1024));
        using var owner = governor.CreateScope();
        var state = new Eval(Parser.Parse($"value=old; read {options} value; status=$?"));

        await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(ShellValue.FromText("old"), state.Variables["value"]);
        Assert.Equal(ShellValue.FromText("1"), state.Variables["status"]);
        Assert.Equal("read: shared memory budget exhausted\n", host.StandardError.TextContent);
        Assert.Equal(64 * 1024, host.ReadBytesConsumed);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadCountCancellationSettlesTheBorrowBeforeUnwinding()
    {
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        await using var host = new PendingReadHost { MemoryBudget = governor };
        host.SetStandardInput("value\n");
        var state = new Eval(Parser.Parse("value=old; read -N2 value"));
        state.Memory.Budget = owner;
        var evaluation = state.EnsureRunning(cancellation.Token);
        var read = Assert.IsType<ReadInputOperation>(await state.GetNextStep());
        var dispatch = ShellOperationDispatcher.DispatchAsync(read, host, cancellation.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var endedBeforeHost = evaluation.IsCompleted;
        host.Release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.GetNextStep());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.False(endedBeforeHost);
        Assert.Equal(0, host.ReadBytesConsumed);
        Assert.Equal(ShellValue.FromText("old"), state.Variables["value"]);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadArrayLeavesTheTargetUntilACanceledInputBorrowSettles()
    {
        var state = new Eval(Parser.Parse("a=(old); read -a a"));
        using var cancellation = new CancellationTokenSource();
        var evaluation = state.EnsureRunning(cancellation.Token);
        var read = Assert.IsType<ReadLineOperation>(await state.GetNextStep());
        cancellation.Cancel();
        var next = state.GetNextStep();
        var completedBeforeRead = await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(100))) == next;
        read.SetResult(ReadResult.Success("one two\n"u8.ToArray(), [], reachedEoF: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.False(completedBeforeRead);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["a"]));
    }

    // Recorded byte-stream contracts; no fresh native comparison is implied.
    [Theory]
    [InlineData("index=0; read index 'arr[index]' <<< '2 value'; printf '<%s><%s><%s>' \"$index\" \"${arr[0]-missing}\" \"${arr[2]}\"", 0, "3C323E3C6D697373696E673E3C76616C75653E", "")]
    [InlineData("index=2; key='a=b'; declare -A map; read 'arr[index]' 'map[$key]' <<< 'indexed associated'; printf '<%s><%s>' \"${arr[2]}\" \"${map[a=b]}\"", 0, "3C696E64657865643E3C6173736F6369617465643E", "")]
    [InlineData("a=(old keep); i=0; read -r 'a[i++]' <<< value; printf '<%s><%s><%s>' \"$i\" \"${a[0]}\" \"${a[1]}\"", 0, "3C313E3C76616C75653E3C6B6565703E", "")]
    [InlineData("declare -A a; k=key; declare -n ref='a[$k]'; read -r ref <<< value; printf '<%s>' \"${a[key]}\"", 0, "3C76616C75653E", "")]
    [InlineData("declare -A a; read -r $'a[\\377]' <<< $'\\376'; printf '<%s><%s>' \"${!a[@]}\" \"${a[@]}\"", 0, "3CFF3E3CFE3E", "")]
    [InlineData("names='first second'; read $names <<< 'one two'; printf '<%s><%s>' \"$first\" \"$second\"", 0, "3C6F6E653E3C74776F3E", "")]
    [InlineData("read '' <<< x; printf '<%s>' \"$?\"", 0, "3C313E", "6C696E6520313A20726561643A2060273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("{ read 1bad later; printf '<%s><%s>' \"$?\" \"${later-unset}\"; read next; printf '<%s>' \"$next\"; } <<< $'one two\\nnext'", 0, "3C313E3C756E7365743E3C6F6E652074776F3E", "6C696E6520313A20726561643A206031626164273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("{ read first 1bad later; printf '<%s><%s><%s>' \"$?\" \"$first\" \"${later-unset}\"; read next; printf '<%s>' \"$next\"; } <<< $'one two three\\nnext'", 0, "3C313E3C6F6E653E3C756E7365743E3C6E6578743E", "6C696E6520313A20726561643A206031626164273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("read 'a[-1]' later <<< 'one two'; printf '<%s><%s>' \"$?\" \"${later-unset}\"", 0, "3C313E3C756E7365743E", "6C696E6520313A20615B2D315D3A20626164206172726179207375627363726970740A")]
    [InlineData("readonly a=old; read a later <<< 'one two'; printf '<%s><%s><%s>' \"$?\" \"$a\" \"${later-unset}\"", 0, "3C313E3C6F6C643E3C756E7365743E", "6C696E6520313A20613A20726561646F6E6C79207661726961626C650A")]
    [InlineData("a=old; declare -rn ref=a; read ref <<< new; printf '<%s><%s>' \"$?\" \"$a\"", 0, "3C303E3C6E65773E", "")]
    [InlineData("i=0; readonly -a a=(old); read 'a[i++]' <<< value; printf '<%s><%s><%s>' \"$?\" \"$i\" \"${a[0]}\"", 0, "3C313E3C313E3C6F6C643E", "6C696E6520313A20613A20726561646F6E6C79207661726961626C650A")]
    [InlineData("i=outer; i=0 read 'a[i++]' <<< value; printf '<%s><%s>' \"$i\" \"${a[0]}\"", 0, "3C6F757465723E3C76616C75653E", "")]
    [InlineData("read first 'a[${missing:?bad}]' <<< 'one two'; printf next", 1, "", "6C696E6520313A206D697373696E673A206261640A")]
    [InlineData("read first 'a[1/0]' <<< 'one two'; printf next", 1, "", "6C696E6520313A20312F303A206469766973696F6E206279203020286572726F7220746F6B656E20697320223022290A")]
    [InlineData("a=(old keep); read a </dev/null; printf '<%s><%s><%s>' \"$?\" \"${a[0]}\" \"${a[1]}\"", 0, "3C313E3C3E3C6B6565703E", "")]
    [InlineData("a=(old keep); read a <<< new; printf '<%s><%s>' \"${a[0]}\" \"${a[1]}\"", 0, "3C6E65773E3C6B6565703E", "")]
    [InlineData("declare -n ref; read ref <<< target; printf '<%s><%s>' \"$?\" \"${!ref}\"", 0, "3C303E3C7461726765743E", "")]
    [InlineData("{ read 'a[$(read next; printf \"%s\" \"$next\")]'; read tail; printf '<%s><%s>' \"${a[2]}\" \"$tail\"; } <<< $'value\\n2\\ntail'", 0, "3C76616C75653E3C7461696C3E", "")]
    [InlineData("readonly stop=old; read first stop later <<< 'one two three'; printf '<%s><%s><%s>' \"$?\" \"$first\" \"${later-unset}\"", 0, "3C313E3C6F6E653E3C756E7365743E", "6C696E6520313A2073746F703A20726561646F6E6C79207661726961626C650A")]
    [InlineData("readonly a; { read a; read next; printf '<%s>' \"$next\"; } <<< $'value\\nnext'", 0, "3C6E6578743E", "6C696E6520313A20613A20726561646F6E6C79207661726961626C650A")]
    [InlineData("{ read 'a[]'; read next; printf '<%s>' \"$next\"; } <<< $'value\\nnext'", 0, "3C76616C75653E", "6C696E6520313A20726561643A2060615B5D273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("{ read 'a[-1]'; read next; printf '<%s>' \"$next\"; } <<< $'value\\nnext'", 0, "3C6E6578743E", "6C696E6520313A20615B2D315D3A20626164206172726179207375627363726970740A")]
    [InlineData("{ read $'\\377'; read next; printf '<%s>' \"$next\"; } <<< $'value\\nnext'", 0, "3C76616C75653E", "6C696E6520313A20726561643A2060FF273A206E6F7420612076616C6964206964656E7469666965720A")]
    public async Task SelectsTargetsAfterInputAndPriorWrites(
        string script, int exitCode, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("""a=outer; f(){ local -n r=a; a=prefix read -r r <<<new; }; f; printf '<%s>' "$a" """, "<new>")]
    [InlineData("""a=outer; f(){ local -n r=a; r=prefix read -r r <<<new; }; f; printf '<%s>' "$a" """, "<new>")]
    [InlineData("""a=outer; f(){ local -n r=a; a=prefix read -r r </dev/null; printf '<%s><%s>' "$?" "$a"; }; f""", "<1><>")]
    [InlineData("""a=outer; f(){ local -n r=a; g(){ a=prefix read -r r <<<new; }; g; }; f; printf '<%s>' "$a" """, "<new>")]
    [InlineData("""a=outer; f(){ local a=local; local -n r=a; a=prefix read -r r <<<new; printf '<%s>' "$a"; }; f; printf '<%s>' "$a" """, "<new><outer>")]
    [InlineData("""a=outer; f(){ local -n r=a; a=prefix read -r a r <<< 'first last'; }; f; printf '<%s>' "$a" """, "<last>")]
    [InlineData("""a=outer; f(){ local -n r=a; a=prefix read -r r a <<< 'first last'; }; f; printf '<%s>' "$a" """, "<first>")]
    [InlineData("""a=outer; f(){ local -n r=a; a=2 read -r r 'arr[a]' <<< '4 slot'; }; f; printf '<%s><%s><%s>' "$a" "${arr[2]-missing}" "${arr[4]-missing}" """, "<4><slot><missing>")]
    [InlineData("""a=(outer keep); f(){ local -n r=a; a=prefix read -r r <<<new; }; f; printf '<%s>' "${a[@]}" """, "<new><keep>")]
    [InlineData("""declare -A a=([0]=outer [keep]=keep); f(){ local -n r=a; a=prefix read -r r <<<new; }; f; printf '<%s><%s>' "${a[0]}" "${a[keep]}" """, "<new><keep>")]
    [InlineData("""a=outer; a=prefix read -r a <<<new; printf '<%s>' "$a" """, "<outer>")]
    [InlineData("""a=outer; declare -n r=a; a=prefix read -r r <<<new; printf '<%s>' "$a" """, "<outer>")]
    [InlineData("""a=(outer keep); i=1; f(){ local -n r='a[i++]'; a=prefix read -r r <<<new; }; f; printf '<%s><%s><%s>' "$i" "${a[0]}" "${a[1]}" """, "<2><outer><keep>")]
    [InlineData("""a=outer; f(){ local -n r=a; b=prefix read -r r <<<new; }; f; printf '<%s>' "$a" """, "<new>")]
    public async Task ReadSelectsOutputsAcrossTemporaryBindings(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task ReadRetainsLocalNamerefFieldsAndConsumesOneRecord()
    {
        const string script = """
            a=outer
            f() {
                local -n r=a
                {
                    a=prefix read -r r second
                    printf '<%s><%s><%s>' "$?" "$a" "$second"
                    read -r tail
                    printf '<%s>' "$tail"
                } < input
            }
            f
            """;
        var result = await EndToEndHarness.RunScriptAsync(script,
            EndToEndHarness.BuildTree([("/input", "new second\nremaining\n")]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0><new><second><remaining>", result.Host.StandardOutput.TextContent);
        Assert.Empty(result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    private sealed class PendingReadHost : MockFileSystem
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DescriptorReadBuffer> ReadAsync(
            FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            return await base.ReadAsync(descriptor, request, CancellationToken.None);
        }
    }
}
