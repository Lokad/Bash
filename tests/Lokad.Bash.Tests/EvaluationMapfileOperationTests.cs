using System.Reflection;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    private const string MapfileUsageArguments = "[-t] [-d delim] [-n count] [-s count] [-O origin] [-u fd] [-C callback] [-c quantum] [--] [array]";

    [Fact]
    public async Task Evaluation_MapfileBuiltin_ReadsAllLinesInBatches()
    {
        var script = Parser.Parse("mapfile arr");
        var state = new Eval(script);

        var firstRead = await NextReadAsync(state);
        Assert.Equal(0, firstRead.FileDescriptor.Id);
        Assert.Equal(128, firstRead.Limit);
        using var firstMemory = Assert.IsAssignableFrom<IMemoryBudget>(firstRead.DestinationBudget).Reserve(512);
        firstRead.SetResult(ReadResult.Success(Utf8Bytes("a\nb\n"), [2], reachedEoF: false));

        var secondRead = await NextReadAsync(state);
        Assert.Equal(128, secondRead.Limit);
        Assert.Equal(0, firstMemory.Bytes);
        secondRead.SetResult(ReadResult.Success(Utf8Bytes("c"), [], reachedEoF: true));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(
            new[] { ShellValue.FromText("a\n"), ShellValue.FromText("b\n"), ShellValue.FromText("c") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task MapfileQuantumAloneKeepsTheRecordLimitAndOrigin()
    {
        var state = new Eval(Parser.Parse("arr=(old); mapfile -c1 -tn2 -O2 arr"));
        var read = await NextReadAsync(state);

        Assert.Equal(2, read.Limit);
        read.SetResult(ReadResult.Success(Utf8Bytes("first\nsecond\n"), [6], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new[] { ShellValue.FromText("old"), ShellValue.Empty,
            ShellValue.FromText("first"), ShellValue.FromText("second") }, state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task MapfileCallbackSeesThePrefixBeforeReadingTheNextBatch()
    {
        var state = new Eval(Parser.Parse("""
            cb(){ printf '<%s><%s><%s>' "$1" "$2" "${arr[0]}"; }
            arr=(old)
            mapfile -t -n3 -c2 -C cb arr
            """));
        var first = await NextReadAsync(state);
        Assert.Equal(2, first.Limit);
        using var batchMemory = Assert.IsAssignableFrom<IMemoryBudget>(first.DestinationBudget).Reserve(512);
        first.SetResult(ReadResult.Success(Utf8Bytes("first\nsecond\n"), [6], reachedEoF: false));

        var callback = await NextWriteAsync(state);
        Assert.Equal("<1><second><first>", Text(callback.Content));
        Assert.Equal(ShellValue.FromText("first"), Assert.Single(state.IndexedArrays["arr"]));
        Assert.Equal(512, batchMemory.Bytes);
        callback.SetResult(DescriptorWriteResult.Open);

        var tail = await NextReadAsync(state);
        Assert.Equal(1, tail.Limit);
        Assert.Equal(0, batchMemory.Bytes);
        Assert.Equal(ShellValue.FromText("second"), state.IndexedArrays["arr"][1]);
        tail.SetResult(ReadResult.Success(Utf8Bytes("third\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("third"), state.IndexedArrays["arr"][2]);
    }

    [Theory]
    [InlineData("break", "<first>[callback]<tail:0:three><values:one two>")]
    [InlineData("continue", "<first>[callback]<second>[callback]<tail:1:><values:three four>")]
    public async Task ArrayReaderSettlesTheRequestedRecordsBeforeApplyingCallbackLoopControl(string action, string output)
    {
        await using var host = new TestHost();
        host.AddFile("/records", "one\ntwo\nthree\nfour\n");
        var result = await host.RunAsync($$"""
            exec 3<records
            for step in first second; do
                printf '<%s>' "$step"
                readarray -u 3 -tn2 -c1 -C 'printf "[callback]"; {{action}}; :' values
                printf unreachable
            done
            IFS= read -r -u 3 tail
            printf '<tail:%s:%s><values:%s>' "$?" "$tail" "${values[*]}"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("break", false, 1, "[callback]")]
    [InlineData("continue", false, 1, "[callback]")]
    [InlineData("break", true, 0, "[callback]<tail:three><exit:0:one two>")]
    public async Task ArrayReaderAppliesErrexitToUntestedLoopControlFailures(
        string command, bool tested, int status, string output)
    {
        await using var host = new TestHost();
        host.AddFile("/records", "one\ntwo\nthree\n");
        var action = tested ? $"{command} 0 || :" : $"{command} 0";
        var result = await host.RunAsync($$"""
            set -e
            trap 'printf "<exit:%s:%s>" "$?" "${values[*]-unset}"' EXIT
            exec 3<records
            for step in once; do
                mapfile -u3 -tn2 -c1 -C 'printf "[callback]"; {{action}}; :' values
                printf unreachable
            done
            IFS= read -r -u3 tail
            printf '<tail:%s>' "$tail"
            """, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal($"line 5: {command}: 0: loop count out of range\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapfileSettlesAnInputBatchAfterCallbackRequestsBreak(bool cancel)
    {
        var state = new Eval(Parser.Parse("""
            values=(prior)
            for step in once; do
                mapfile -tn3 -c1 -C 'break; :' values
                printf unreachable
            done
            """));
        using var cancellation = new CancellationTokenSource();
        var evaluation = state.EnsureRunning(cancellation.Token);
        var first = await NextReadAsync(state);
        Assert.Equal(1, first.Limit);
        using var firstMemory = Assert.IsAssignableFrom<IMemoryBudget>(first.DestinationBudget).Reserve(512);
        first.SetResult(ReadResult.Success(Utf8Bytes("one\n"), [], reachedEoF: false));

        var rest = await NextReadAsync(state);
        Assert.Equal(2, rest.Limit);
        Assert.Equal(0, firstMemory.Bytes);
        Assert.Equal(ShellValue.FromText("one"), Assert.Single(state.IndexedArrays["values"]));
        using var restMemory = Assert.IsAssignableFrom<IMemoryBudget>(rest.DestinationBudget).Reserve(512);
        if (cancel)
        {
            cancellation.Cancel();
            var next = state.GetNextStep();
            Assert.False(next.IsCompleted);
            Assert.Equal(512, restMemory.Bytes);
            rest.SetResult(ReadResult.Success(Utf8Bytes("two\nthree\n"), [4], reachedEoF: false));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
            Assert.Equal(0, restMemory.Bytes);
            Assert.Equal(ShellValue.FromText("prior"), Assert.Single(state.IndexedArrays["values"]));
            return;
        }

        rest.SetResult(ReadResult.Success(Utf8Bytes("two\nthree\n"), [4], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, restMemory.Bytes);
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new[] { ShellValue.FromText("one"), ShellValue.FromText("two"), ShellValue.FromText("three") },
            state.IndexedArrays["values"]);
    }

    [Theory]
    [InlineData("mixed", "MIXED")]
    [InlineData("UPPER", "UPPER")]
    [InlineData("", "")]
    public async Task MapfileCallbackStoresOwnedRecordsUsingTheReachedAttributes(string record, string expected)
    {
        var state = new Eval(Parser.Parse("mapfile -tc2 -C 'declare -u arr; :' arr"));
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        var input = Utf8Bytes("first\n" + record + "\n").ToArray();
        var read = await NextReadAsync(state);
        using var borrowed = Assert.IsAssignableFrom<IMemoryBudget>(read.DestinationBudget).Reserve(input.Length);
        read.SetResult(ReadResult.Success(input, [6], reachedEoF: true));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(0, borrowed.Bytes);
        input.AsSpan().Fill((byte)'?');
        var records = state.IndexedArrays["arr"];
        Assert.Equal(new[] { ShellValue.FromText("first"), ShellValue.FromText(expected) }, records);
        if (record.Length == 0)
            Assert.True(records[1].Bytes.Equals(ShellValue.Empty.Bytes));
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadarrayCallbackCanConsumeTheNextRecordFromTheSelectedDescriptor()
    {
        await using var host = new TestHost();
        host.AddFile("/input", "a\nb' with spaces\nc\nd\ne\n");
        var result = await host.RunAsync("""
            cb(){ IFS= read -r skipped <&3; printf '<%s><%s><%s>' "$1" "$2" "$skipped"; }
            exec 3<input
            readarray -tc2 -C cb -n3 arr <&3
            printf '[%s]' "${arr[@]}"
            IFS= read -r tail <&3
            printf '<tail:%s>' "$tail"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1><b' with spaces><c>[a][b' with spaces][d]<tail:e>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task MapfileCancellationSettlesTheCallbackBeforeRestoringTheArray()
    {
        var state = new Eval(Parser.Parse("""
            arr=(old)
            mapfile -tc2 -C 'printf "<%s:%s>"' arr
            """));
        using var cancellation = new CancellationTokenSource();
        var evaluation = state.EnsureRunning(cancellation.Token);
        var read = await NextReadAsync(state);
        using var borrowed = Assert.IsAssignableFrom<IMemoryBudget>(read.DestinationBudget).Reserve(512);
        read.SetResult(ReadResult.Success(Utf8Bytes("first\nsecond\n"), [6], reachedEoF: false));
        var callback = await NextWriteAsync(state);
        Assert.Equal("<1:second>", Text(callback.Content));

        cancellation.Cancel();
        var next = state.GetNextStep();
        Assert.False(next.IsCompleted);
        Assert.Equal(512, borrowed.Bytes);
        Assert.Equal(ShellValue.FromText("first"), Assert.Single(state.IndexedArrays["arr"]));
        callback.SetResult(DescriptorWriteResult.Open);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.Equal(0, borrowed.Bytes);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }

    [Theory]
    [InlineData("mapfile")]
    [InlineData("readarray")]
    public async Task ArrayReaderCallbackRefusalRestoresTheArrayAndReportsItsName(string builtin)
    {
        var state = new Eval(Parser.Parse($"arr=(old); {builtin} -tc2 -C : arr"));
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;

        var first = await NextReadAsync(state);
        first.SetResult(ReadResult.Success(Utf8Bytes("first\n"), [], reachedEoF: false));

        var second = await NextReadAsync(state);
        Assert.Equal(1, second.Limit);
        Assert.Equal(ShellValue.FromText("first"), Assert.Single(state.IndexedArrays["arr"]));
        var record = Utf8Bytes(new string('x', 4096) + "\n");
        using var borrowed = Assert.IsAssignableFrom<IMemoryBudget>(second.DestinationBudget).Reserve(record.Length);
        using var occupied = governor.Reserve(governor.LimitBytes - governor.ReservedBytes - 2048);
        second.SetResult(ReadResult.Success(record, [], reachedEoF: false));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal($"{builtin}: shared memory budget exhausted\n", Text(diagnostic.Content));
        Assert.Equal(0, borrowed.Bytes);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
        occupied.Dispose();
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task MapfileRestoresTheArrayWhenBorrowedRecordConversionIsRefused()
    {
        var state = new Eval(Parser.Parse("declare -au arr=(old); mapfile -tc3 -C : arr"));
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;

        var first = await NextReadAsync(state);
        first.SetResult(ReadResult.Success(Utf8Bytes("first\n"), [], reachedEoF: false));
        var second = await NextReadAsync(state);
        Assert.Equal(ShellValue.FromText("FIRST"), Assert.Single(state.IndexedArrays["arr"]));

        var record = Utf8Bytes(new string('x', 4096) + "\n");
        using var borrowed = Assert.IsAssignableFrom<IMemoryBudget>(second.DestinationBudget).Reserve(record.Length);
        using var occupied = governor.Reserve(governor.LimitBytes - governor.ReservedBytes - 2048);
        second.SetResult(ReadResult.Success(record, [], reachedEoF: true));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("mapfile: shared memory budget exhausted\n", Text(diagnostic.Content));
        Assert.Equal(0, borrowed.Bytes);
        Assert.Equal(ShellValue.FromText("OLD"), Assert.Single(state.IndexedArrays["arr"]));
        occupied.Dispose();
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task MapfileCallbackHonorsErrexitWhenTheReadIsTested()
    {
        var result = await EndToEndHarness.RunScriptAsync("""
            set -e
            if mapfile -t -c2 -C false arr <<< $'a\nb\nc'; then printf '<then>'; else printf '<else>'; fi
            printf '<after>'
            """, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_CountBoundsTheFinalBatchWithoutAnEofRead()
    {
        var state = new Eval(Parser.Parse("arr=([0]=keep); mapfile -tn129 -O2 arr"));
        var first = await NextReadAsync(state);
        Assert.Equal(128, first.Limit);
        first.SetResult(ReadResult.Success(Utf8Bytes(new string('\n', 128)),
            Enumerable.Range(1, 127).ToArray(), reachedEoF: false));

        var last = await NextReadAsync(state);
        Assert.Equal(1, last.Limit);
        Assert.Equal(ShellValue.FromText("keep"), Assert.Single(state.IndexedArrays["arr"]));
        last.SetResult(ReadResult.Success(Utf8Bytes("last\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(131, state.IndexedArrays["arr"].Count);
        Assert.Equal(ShellValue.FromText("keep"), state.IndexedArrays["arr"][0]);
        Assert.Equal(ShellValue.FromText("last"), state.IndexedArrays["arr"][130]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapfileAdmitsRecordListOverlapAndRefundsOldBacking(bool refuseGrowth)
    {
        var budget = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = budget.CreateScope();
        var state = new Eval(Parser.Parse("arr=(old); mapfile -tn130 arr"));
        state.Memory.Budget = owner;

        var first = await NextReadAsync(state);
        var before = budget.ReservedBytes;
        var lines = Utf8Bytes(new string('\n', 128));
        using var firstBatch = Assert.IsAssignableFrom<IMemoryBudget>(first.DestinationBudget).Reserve(lines.Length);
        first.SetResult(ReadResult.Success(lines, Enumerable.Range(1, 127).ToArray(), reachedEoF: false));

        var growth = await NextReadAsync(state);
        Assert.Equal(2, growth.Limit);
        Assert.Equal(0, firstBatch.Bytes);
        var beforeGrowth = budget.ReservedBytes;
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));

        var replacement = MemoryCapacity.ArrayBackingBytes<ShellValue>(256);
        using var held = budget.Reserve(refuseGrowth ? budget.LimitBytes - beforeGrowth - replacement + 1 : 0);
        growth.SetResult(ReadResult.Success(Utf8Bytes("\n"), [], reachedEoF: false));
        var afterGrowth = 0L;
        if (refuseGrowth)
        {
            var diagnostic = await NextWriteAsync(state);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Equal("mapfile: shared memory budget exhausted\n", Text(diagnostic.Content));
            diagnostic.SetResult(DescriptorWriteResult.Open);
        }
        else
        {
            var last = await NextReadAsync(state);
            Assert.Equal(1, last.Limit);
            afterGrowth = budget.ReservedBytes;
            last.SetResult(ReadResult.Success(Utf8Bytes("last\n"), [], reachedEoF: false));
        }
        Assert.Null(await NextStepAsync(state));

        var result = state.IndexedArrays["arr"];
        Assert.Equal(refuseGrowth ? 2 : 0, state.LastExitCode);
        if (refuseGrowth)
            Assert.Equal(ShellValue.FromText("old"), Assert.Single(result));
        else
        {
            Assert.Equal(130, result.Count);
            Assert.Equal(ShellValue.FromText("last"), result[129]);
            Assert.Equal(replacement, afterGrowth - before);
        }
        Assert.Equal(MemoryCapacity.ArrayBackingBytes<ShellValue>(128), beforeGrowth - before);
        owner.Dispose();
        held.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(false, true, "", "")]
    [InlineData(true, true, "", "")]
    [InlineData(false, false, "", "")]
    [InlineData(true, false, "", "")]
    [InlineData(false, true, "", "-C : -c2")]
    [InlineData(true, true, "", "-C : -c2")]
    [InlineData(false, true, "declare -ai arr; ", "")]
    [InlineData(true, true, "declare -ai arr; ", "")]
    public async Task ArrayReadersReuseImmutableBytesForSkippedAndOrdinaryRecords(
        bool customDelimiter, bool canRetain, string setup, string callback)
    {
        var reader = customDelimiter ? "readarray -d:" : "mapfile";
        var separator = customDelimiter ? (byte)':' : (byte)'\n';
        var stagedConsumer = setup.Length != 0 || callback.Length != 0;
        byte[] last = stagedConsumer ? [(byte)'7'] : [(byte)'7', 0xff, 13];
        byte[] input = [(byte)'9', separator, (byte)'5', separator, .. last];
        var state = new Eval(Parser.Parse($"{setup}{reader} -ts1 -n2 {callback} arr"));
        var memory = new MemoryGovernor(1024 * 1024);
        using var evaluation = memory.CreateScope();
        using var readMemory = memory.CreateScope();
        var observations = new List<(int Bytes, long Admission)>();
        var retained = new List<bool>();
        var offset = 0;
        var beforeBorrow = 0L;
        var buffer = new DescriptorReadBufferOwner(Commit, readMemory);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory, ReadOverride = Read };

        await new Engine().RunAsync(state, host, evaluation, CancellationToken.None);

        const int OffsetBytes = 32 + sizeof(int);
        var retainCollected = canRetain && !stagedConsumer;
        Assert.Equal([canRetain, retainCollected], retained);
        Assert.Equal<(int, long)>([
            (2, canRetain ? 0 : 2),
            (input.Length - 2, OffsetBytes + (retainCollected ? 0 : input.Length - 2))
        ], observations);
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal([ShellValue.FromText("5"), ShellValue.FromBytes(last)], state.IndexedArrays["arr"]);
        Assert.Equal(input.Length, offset);
        evaluation.Dispose();
        Assert.Equal(0, memory.ReservedBytes);

        void Commit(int consumed)
        {
            observations.Add((consumed, memory.ReservedBytes - beforeBorrow));
            offset += consumed;
        }

        ValueTask<DescriptorReadBuffer> Read(CancellationToken _)
        {
            Assert.True(offset < input.Length);
            beforeBorrow = memory.ReservedBytes;
            ReadOnlyMemory<byte> unread = input.AsMemory(offset);
            var result = ByteReadResult.Success(unread.Length, reachedEoF: true);
            var scratch = canRetain ? null : unread.ToArray();
            var borrow = buffer.Borrow(scratch is null ? unread : scratch, result, canRetain);
            borrow.ReleaseWith(() =>
            {
                retained.Add(buffer.WasRetained);
                if (scratch != null)
                    Array.Clear(scratch);
            });
            return ValueTask.FromResult(borrow);
        }
    }

    [Theory]
    [InlineData("arr=(old)", "old")]
    [InlineData("declare -ai arr=(99)", "99")]
    public async Task Evaluation_MapfileBuiltin_RefundsSkippedBatchesBeforeCollecting(string setup, string previous)
    {
        var state = new Eval(Parser.Parse($"counter=0; {setup}; mapfile -ts129 -n1 arr"));

        var firstSkip = await NextReadAsync(state);
        Assert.Equal(128, firstSkip.Limit);
        using var firstMemory = Assert.IsAssignableFrom<IMemoryBudget>(firstSkip.DestinationBudget).Reserve(512);
        firstSkip.SetResult(ReadResult.Success(Utf8Bytes(new string('\n', 128)),
            Enumerable.Range(1, 127).ToArray(), reachedEoF: false));

        var finalSkip = await NextReadAsync(state);
        Assert.Equal(1, finalSkip.Limit);
        Assert.Equal(0, firstMemory.Bytes);
        using var finalMemory = Assert.IsAssignableFrom<IMemoryBudget>(finalSkip.DestinationBudget).Reserve(512);
        finalSkip.SetResult(ReadResult.Success(Utf8Bytes("counter++\n"), [], reachedEoF: false));

        var retained = await NextReadAsync(state);
        Assert.Equal(1, retained.Limit);
        Assert.Equal(0, finalMemory.Bytes);
        Assert.Equal(ShellValue.FromText(previous), Assert.Single(state.IndexedArrays["arr"]));
        retained.SetResult(ReadResult.Success(Utf8Bytes("7\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("7"), Assert.Single(state.IndexedArrays["arr"]));
        Assert.Equal("0", GetVariable(state, "counter"));
    }

    [Theory]
    [InlineData("mapfile -t", '\n')]
    [InlineData("readarray -td:", ':')]
    public async Task IntegerArrayReaderRetainsOnlyBatchesWithBorrowedValues(string command, char delimiter)
    {
        var budget = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = budget.CreateScope();
        var state = new Eval(Parser.Parse($"counter=1; declare -ai arr=(99); {command} -n5 arr"));
        state.Memory.Budget = owner;

        var emptyAfterNormalization = -1L;
        var mixedWhileReading = -1L;
        IReadOnlyList<ShellValue> prior = [];
        var counterWhileReading = string.Empty;
        using var empty = await CompleteBatchAsync(new string(delimiter, 2), [1], () => { });
        using var mixed = await CompleteBatchAsync($"counter++{delimiter}{delimiter}", [10], () =>
            emptyAfterNormalization = empty.Bytes);
        using var last = await CompleteBatchAsync($"counter{delimiter}", [], () =>
        {
            mixedWhileReading = mixed.Bytes;
            prior = state.IndexedArrays["arr"];
            counterWhileReading = GetVariable(state, "counter");
        });

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(0, emptyAfterNormalization);
        Assert.Equal(512, mixedWhileReading);
        Assert.Equal(ShellValue.FromText("99"), Assert.Single(prior));
        Assert.Equal("1", counterWhileReading);
        Assert.Equal(new[] { "0", "0", "1", "0", "2" }.Select(ShellValue.FromText), state.IndexedArrays["arr"]);
        Assert.Equal("2", GetVariable(state, "counter"));
        Assert.Equal(0, mixed.Bytes);
        Assert.Equal(0, last.Bytes);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);

        async Task<IMemoryReservation> CompleteBatchAsync(string input, int[] offsets, Action check)
        {
            var step = await NextStepAsync(state);
            check();
            var bytes = Utf8Bytes(input);
            if (delimiter == '\n')
            {
                var request = Assert.IsType<ReadLineOperation>(step);
                var charge = Assert.IsAssignableFrom<IMemoryBudget>(request.DestinationBudget).Reserve(512);
                request.SetResult(ReadResult.Success(bytes, offsets, reachedEoF: false));
                return charge;
            }

            var records = Assert.IsType<ReadRecordBatchOperation>(step);
            var reservation = records.DestinationBudget.Reserve(512);
            records.SetResult(new DescriptorRecordBatch(bytes, offsets, ByteReadResult.Success(bytes.Length, reachedEoF: false)));
            return reservation;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntegerArrayReaderReleasesConsumedBatchBeforeLaterArithmetic(bool separateBatches)
    {
        var budget = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = budget.CreateScope();
        var state = new Eval(Parser.Parse("n=1; declare -ai arr=(99); mapfile -tn3 arr"));
        state.Memory.Budget = owner;

        var first = await NextReadAsync(state);
        using var initial = Assert.IsAssignableFrom<IMemoryBudget>(first.DestinationBudget).Reserve(512);
        first.SetResult(separateBatches
            ? ReadResult.Success(Utf8Bytes("n++\n\n"), [4], reachedEoF: false)
            : ReadResult.Success(Utf8Bytes("n++\n\n1/0\n"), [4, 5], reachedEoF: false));
        using var later = separateBatches ? await CompleteLaterReadAsync() : null;

        var diagnostic = await NextWriteAsync(state);
        var initialWhileWriting = initial.Bytes;
        var failingWhileWriting = (later ?? initial).Bytes;
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Contains("division by 0", Text(diagnostic.Content), StringComparison.Ordinal);
        Assert.Equal(separateBatches ? 0 : 512, initialWhileWriting);
        Assert.Equal(512, failingWhileWriting);
        Assert.Equal("2", GetVariable(state, "n"));
        Assert.Equal(new[] { "1", "0" }.Select(ShellValue.FromText), state.IndexedArrays["arr"]);
        Assert.Equal(0, initial.Bytes);
        Assert.Equal(0, (later ?? initial).Bytes);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);

        async Task<IMemoryReservation> CompleteLaterReadAsync()
        {
            var read = await NextReadAsync(state);
            var reservation = Assert.IsAssignableFrom<IMemoryBudget>(read.DestinationBudget).Reserve(512);
            read.SetResult(ReadResult.Success(Utf8Bytes("1/0\n"), [], reachedEoF: false));
            return reservation;
        }
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_PreservesTheArrayWhenReadingTheSkipPrefixFails()
    {
        var state = new Eval(Parser.Parse("arr=(old); mapfile -ts2 -n1 arr"));
        var skip = await NextReadAsync(state);
        Assert.Equal(2, skip.Limit);
        using var memory = Assert.IsAssignableFrom<IMemoryBudget>(skip.DestinationBudget).Reserve(512);
        skip.SetResult(ReadResult.Failure(new OperationError("input failed\n")));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
        Assert.Equal(0, memory.Bytes);
    }

    [Theory]
    [InlineData(0U, 0, "saved", 1)]
    [InlineData(0U, 1024, "0", 1024)]
    [InlineData(3U, 1024, "saved", 1025)]
    [InlineData(uint.MaxValue - 2, 3, "saved", 4)]
    [InlineData(uint.MaxValue - 1, 3, "2", 3)]
    public void MapfileOriginPresizesScalarPromotionForItsFinalPositions(
        uint origin, int count, string zero, int expectedCount)
    {
        var variables = Variables.CreateEmpty();
        var saved = ShellValue.FromText("saved");
        Assert.True(variables.TrySetScalar("arr", saved));
        var values = Enumerable.Range(0, count).Select(index => ShellValue.FromInteger(index)).ToArray();
        var required = (long)expectedCount * IndexedArray.ElementMemoryBytes;
        var governor = new MemoryGovernor(required);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);

        using (owner.Reserve(1))
        {
            Assert.Throws<MemoryBudgetExceededException>(() => variables.TryUpdateResolvedIndexedArray(
                "arr", origin, values, namerefValue: null, memory, CancellationToken.None, prepaidEntryBytes: 0));
            Assert.Equal(saved, variables.Scalars["arr"]);
            Assert.Equal(0, memory.Workspace.Bytes);
        }
        Assert.True(variables.TryUpdateResolvedIndexedArray(
            "arr", origin, values, namerefValue: null, memory, CancellationToken.None, prepaidEntryBytes: 0));

        var array = variables.IndexedArrays["arr"];
        Assert.Equal(expectedCount, array.Count);
        Assert.Equal(required, memory.Workspace.Bytes);
        Assert.Equal(new Dictionary<long, ShellValue?>(expectedCount).EnsureCapacity(0), Elements(array).EnsureCapacity(0));
        Assert.True(array.TryGet(default, out var actualZero));
        Assert.Equal(ShellValue.FromText(zero), actualZero);
        for (var index = 0; index < count; index++)
        {
            var position = IndexedArrayIndex.FromUnsigned(unchecked(origin + (uint)index));
            Assert.True(array.TryGet(position, out var value));
            Assert.Equal(values[index], value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapfileOriginAdmitsNewEntriesWithoutRechargingOverwrittenPositions(bool append)
    {
        const int records = 1024;
        const int batchSize = 128;
        var origin = append ? records : 0;
        var state = new Eval(Parser.Parse($"mapfile -tn{records} -O{origin} arr"));
        var variables = Assert.IsType<Variables>(typeof(Eval)
            .GetField("_vars", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(state));
        var old = ShellValue.FromText("old");
        Assert.True(variables.TryAssignArray("arr", Enumerable.Repeat(old, records).ToArray(), ArrayKind.Indexed));
        var array = variables.IndexedArrays["arr"];
        var backing = CollectionBacking.ArrayPayloadBytes(Elements(array));
        var budget = new MemoryGovernor(8 * 1024 * 1024);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;
        var lines = Utf8Bytes(string.Concat(Enumerable.Repeat("new\n", batchSize)));
        var offsets = Enumerable.Range(1, batchSize - 1).Select(index => 4 * index).ToArray();

        var read = await NextReadAsync(state);
        for (var offset = batchSize; offset < records; offset += batchSize)
        {
            Assert.Equal(batchSize, read.Limit);
            read.SetResult(ReadResult.Success(lines, offsets, reachedEoF: false));
            read = await NextReadAsync(state);
        }

        state.Memory.ReleaseUnusedCapacity();
        using var held = budget.Reserve(budget.LimitBytes - budget.ReservedBytes - 72 * 1024);
        read.SetResult(ReadResult.Success(lines, offsets, reachedEoF: false));
        var operation = await NextStepAsync(state);
        if (append)
        {
            var diagnostic = Assert.IsType<WriteOperation>(operation);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Equal("mapfile: shared memory budget exhausted\n", Text(diagnostic.Content));
            diagnostic.SetResult(DescriptorWriteResult.Open);
            operation = await NextStepAsync(state);
        }

        Assert.Null(operation);
        Assert.Equal(append ? 2 : 0, state.LastExitCode);
        Assert.Same(array, variables.IndexedArrays["arr"]);
        Assert.Equal(records, array.Count);
        Assert.Equal(backing, CollectionBacking.ArrayPayloadBytes(Elements(array)));
        Assert.All(array.PresentValues, value => Assert.Equal(append ? old : ShellValue.FromText("new"), value));
        owner.Dispose();
        held.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task MapfileOriginAdmitsDictionaryGrowthBeforeKeepingNewRecords(bool integer, bool grow, bool limited)
    {
        var state = new Eval(Parser.Parse("mapfile -tn2 -O \"$origin\" arr"));
        var variables = Assert.IsType<Variables>(typeof(Eval)
            .GetField("_vars", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(state));
        var original = ShellValue.FromText(integer ? "42" : "original");
        Assert.True(variables.TryAssignArray("arr", Enumerable.Repeat(original, 1024).ToArray(), ArrayKind.Indexed));
        var array = variables.IndexedArrays["arr"];
        var elements = Elements(array);
        var capacity = elements.EnsureCapacity(0);
        for (var index = array.Count; index < capacity; index++)
        {
            Assert.True(IndexedArrayIndex.TryCreate(index, out var position));
            Assert.True(variables.TrySetArrayElement("arr", position, original));
        }

        var origin = capacity - (grow ? 1 : 2);
        Assert.True(variables.TrySetScalar("origin", ShellValue.FromText(Integers.FormatNumber(origin))));
        if (integer)
        {
            variables.SetIntegerBinding("arr", true);
            Assert.True(variables.TrySetScalar("visits", ShellValue.FromText("0")));
        }
        var budget = new MemoryGovernor(8 * 1024 * 1024);
        using var scope = budget.CreateScope();
        state.Memory.Budget = scope;
        var read = await NextReadAsync(state);
        Assert.Equal(2, read.Limit);
        state.Memory.ReleaseUnusedCapacity();
        var backing = CollectionBacking.ArrayPayloadBytes(elements);
        // Leave room for the staged integer binding, one workspace increment, and the input list.
        var cloneBytes = integer ? variables.EstimateBindingBytes("arr") : 0;
        using var held = budget.Reserve(limited ? budget.LimitBytes - budget.ReservedBytes - cloneBytes - 72 * 1024 : 0);
        read.SetResult(integer
            ? ReadResult.Success(Utf8Bytes("visits++\narr[origin]+5\n"), [9], reachedEoF: false)
            : ReadResult.Success(Utf8Bytes("first\nsecond\n"), [6], reachedEoF: false));
        var operation = await NextStepAsync(state);
        var diagnostic = operation as WriteOperation;
        if (diagnostic != null)
        {
            diagnostic.SetResult(DescriptorWriteResult.Open);
            operation = await NextStepAsync(state);
        }
        Assert.Null(operation);
        var published = variables.IndexedArrays["arr"];
        Assert.True(IndexedArrayIndex.TryCreate(origin, out var first));
        Assert.True(IndexedArrayIndex.TryCreate(origin + 1, out var second));
        Assert.True(published.TryGet(first, out var firstValue));
        var backingAfter = CollectionBacking.ArrayPayloadBytes(Elements(published));
        if (integer)
            Assert.Equal("1", variables.Scalars["visits"].ToUtf8String());
        if (grow && limited)
        {
            Assert.Equal(backing, backingAfter);
            Assert.Same(array, published);
            Assert.Equal(original, firstValue);
            Assert.False(array.TryGet(second, out _));
            Assert.Equal(capacity, array.Count);
            Assert.Equal(2, state.LastExitCode);
            Assert.NotNull(diagnostic);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Equal("mapfile: shared memory budget exhausted\n", Text(diagnostic.Content));
        }
        else
        {
            Assert.Equal(grow, backingAfter > backing);
            Assert.Equal(!integer, ReferenceEquals(array, published));
            Assert.Equal(ShellValue.FromText(integer ? "0" : "first"), firstValue);
            Assert.True(published.TryGet(second, out var secondValue));
            Assert.Equal(ShellValue.FromText(integer ? "5" : "second"), secondValue);
            Assert.Equal(capacity + (grow ? 1 : 0), published.Count);
            Assert.Equal(0, state.LastExitCode);
            Assert.Null(diagnostic);
        }
    }

    private static Dictionary<long, ShellValue?> Elements(IndexedArray array) =>
        Assert.IsType<Dictionary<long, ShellValue?>>(typeof(IndexedArray)
            .GetField("_elements", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(array));

    [Fact]
    public async Task Evaluation_MapfileBuiltin_PreservesArrayWhenOpaqueInputReportsBadDescriptor()
    {
        var script = Parser.Parse("arr=(old); mapfile arr");
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        read.SetResult(ReadResult.Failure(error: OperationError.BadFileDescriptor));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(
            new[] { ShellValue.FromText("old") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_PartialContentThenFailureDoesNotReplaceArray()
    {
        var script = Parser.Parse("arr=(old); mapfile arr");
        var state = new Eval(script);

        var content = await NextReadAsync(state);
        content.SetResult(ReadResult.Success(Utf8Bytes("staged\n"), [], reachedEoF: false));

        var failure = await NextReadAsync(state);
        failure.SetResult(ReadResult.Failure(error: new OperationError("read error\n")));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(
            new[] { ShellValue.FromText("old") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_CancellationDoesNotReplaceArray()
    {
        var script = Parser.Parse("arr=(old); mapfile arr");
        var state = new Eval(script);
        var read = await NextReadAsync(state);
        var visitor = new TestOperationVisitor([]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ShellOperationDispatcher.DispatchAsync(read, visitor, cancellation.Token));

        Assert.Equal(
            new[] { ShellValue.FromText("old") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_ReadarrayBuiltin_TrimRemovesOnlyNewlineDelimiter()
    {
        var script = Parser.Parse("readarray -t arr");
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        Assert.Equal(0, read.FileDescriptor.Id);
        Assert.Equal(128, read.Limit);
        read.SetResult(ReadResult.Success(Utf8Bytes("x\r\ny\n"), [3], reachedEoF: true));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(
            new[] { ShellValue.FromText("x\r"), ShellValue.FromText("y") },
            state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_ReadsCustomRecordBatchesBeforePublishing()
    {
        var state = new Eval(Parser.Parse("arr=(old); mapfile -td: arr"));

        var first = Assert.IsType<ReadRecordBatchOperation>(await NextStepAsync(state));
        Assert.Equal(FileDescriptor.StdIn, first.Descriptor);
        Assert.Equal((byte)':', first.Delimiter);
        Assert.Equal(128, first.Limit);
        using var firstMemory = first.DestinationBudget.Reserve(512);
        var content = Utf8Bytes("a::");
        first.SetResult(new DescriptorRecordBatch(content, [2], ByteReadResult.Success(content.Length, reachedEoF: false)));

        var second = Assert.IsType<ReadRecordBatchOperation>(await NextStepAsync(state));
        Assert.Equal(0, firstMemory.Bytes);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
        second.SetResult(new DescriptorRecordBatch(default, [], ByteReadResult.EndOfFile));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new[] { ShellValue.FromText("a"), ShellValue.Empty }, state.IndexedArrays["arr"]);
        Assert.Equal(ShellValue.Empty.Bytes, state.IndexedArrays["arr"][1].Bytes);
    }

    [Theory]
    [InlineData("mapfile -t", '\n', "unset 'arr[0]'", "missing")]
    [InlineData("readarray -td:", ':', "arr[0]=new", "new")]
    public async Task MapfileRetiresRecordStorageAfterElementMutation(
        string command, char delimiter, string mutation, string expected)
    {
        var script = Parser.Parse($"{command} arr; {mutation}; printf '%s:%s' \"${{arr[0]-missing}}\" \"${{arr[1]}}\"");
        var shortRecord = await RunAsync(8);
        var longRecord = await RunAsync(128 * 1024);

        Assert.Equal(shortRecord, longRecord);

        async Task<long> RunAsync(int recordLength)
        {
            var governor = new MemoryGovernor(4 * 1024 * 1024);
            await using var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), governor);
            host.SetStandardInput(new string('x', recordLength) + delimiter + "keep" + delimiter);
            using var owner = governor.CreateScope();
            var state = new Eval(script);

            var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected + ":keep", host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
            var retained = governor.ReservedBytes;
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return retained;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" -s2")]
    public async Task Evaluation_MapfileBuiltin_SettlesPendingBoundedBatchBeforeCancellation(string skip)
    {
        var state = new Eval(Parser.Parse($"arr=(old); mapfile -td: -n2 -O0{skip} arr"));
        using var cancellation = new CancellationTokenSource();
        var evaluation = state.EnsureRunning(cancellation.Token);

        var first = Assert.IsType<ReadRecordBatchOperation>(await NextStepAsync(state));
        Assert.Equal(2, first.Limit);
        first.SetResult(new DescriptorRecordBatch(Utf8Bytes("a:"), [], ByteReadResult.Success(2, reachedEoF: false)));
        var last = Assert.IsType<ReadRecordBatchOperation>(await NextStepAsync(state));
        Assert.Equal(1, last.Limit);
        using var pendingMemory = last.DestinationBudget.Reserve(512);

        cancellation.Cancel();
        var next = state.GetNextStep();
        var completedBeforeRead = await Task.WhenAny(next, Task.Delay(TimeSpan.FromMilliseconds(100))) == next;
        var reservedBeforeRead = pendingMemory.Bytes;
        last.SetResult(new DescriptorRecordBatch(Utf8Bytes("b:"), [], ByteReadResult.Success(2, reachedEoF: false)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.False(completedBeforeRead);
        Assert.Equal(512, reservedBeforeRead);
        Assert.Equal(0, pendingMemory.Bytes);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_EmptyInputClearsArrayAndSucceeds()
    {
        var script = Parser.Parse("arr=(old); mapfile arr");
        var state = new Eval(script);

        var read = await NextReadAsync(state);
        read.SetResult(ReadResult.EndOfFile);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Empty(state.IndexedArrays["arr"]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_RejectsAssociativeArrayBeforeReading()
    {
        var script = Parser.Parse("declare -A arr; arr[key]=old; mapfile arr");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: mapfile: arr: not an indexed array\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("old", state.AssociativeArrays["arr"][ShellValue.FromText("key")].ToUtf8String());
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_RejectsUnsupportedShapeBeforeReading()
    {
        const string command = "mapfile -x arr";
        var script = Parser.Parse($"arr=(old); {command}");
        var state = new Eval(script);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        var reason = command.Split(' ')[1] + ": invalid option";
        Assert.Equal($"line 1: mapfile: {reason}\nmapfile: usage: mapfile {MapfileUsageArguments}\n",
            Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));

        Assert.Equal(2, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), state.IndexedArrays["arr"][0]);
    }

    [Fact]
    public async Task Evaluation_MapfileBuiltin_ReadsTheBorrowedLogicalDescriptor()
    {
        var table = new DescriptorTable();
        var handle = new FileDescriptor(9);
        table.Set(new LogicalDescriptor(3), handle);
        var descriptors = new EvaluationDescriptorSetup(table.Capture(), [handle]);
        var state = new Eval(Parser.Parse("mapfile -tn1 -u3 arr"), descriptors,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default,
            new MemoryGovernor(TestMemoryBudget.EvaluationBytes), sourceMemory: null);

        var read = await NextReadAsync(state);
        Assert.Equal(handle, read.FileDescriptor);
        Assert.Equal(1, read.Limit);
        read.SetResult(ReadResult.Success(Utf8Bytes("selected\n"), [], reachedEoF: false));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("selected"), Assert.Single(state.IndexedArrays["arr"]));
        Assert.Equal(handle, table.Resolve(new LogicalDescriptor(3)));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData("mapfile -u9 -u3", false, "line 1: mapfile: 9: invalid file descriptor: Bad file descriptor\n")]
    [InlineData("readarray -u +009", true, "line 1: readarray: 9: invalid file descriptor: Bad file descriptor\n")]
    [InlineData("mapfile -u -1", false, "line 1: mapfile: -1: invalid file descriptor specification\n")]
    public async Task Evaluation_MapfileBuiltin_RejectsInvalidDescriptorBeforeInput(
        string command, bool explicitlyClosed, string error)
    {
        var table = new DescriptorTable();
        var handle = new FileDescriptor(9);
        table.Set(new LogicalDescriptor(3), handle);
        if (explicitlyClosed)
            table.Set(new LogicalDescriptor(9), FileDescriptor.Closed);
        var descriptors = new EvaluationDescriptorSetup(table.Capture(), [handle]);
        var state = new Eval(Parser.Parse($"MAPFILE=(old); {command}"), descriptors,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default,
            new MemoryGovernor(TestMemoryBudget.EvaluationBytes), sourceMemory: null);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal(error, Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["MAPFILE"]));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Theory]
    [InlineData("mapfile -d : -d", "mapfile", "-d")]
    [InlineData("readarray -td", "readarray", "-d")]
    [InlineData("mapfile -n", "mapfile", "-n")]
    [InlineData("readarray -tO", "readarray", "-O")]
    [InlineData("mapfile -u", "mapfile", "-u")]
    [InlineData("mapfile -s", "mapfile", "-s")]
    [InlineData("mapfile -C", "mapfile", "-C")]
    [InlineData("readarray -tc", "readarray", "-c")]
    public async Task Evaluation_MapfileBuiltin_RejectsMissingOptionOperandBeforeReading(string command, string builtin, string option)
    {
        var state = new Eval(Parser.Parse($"arr=(old); {command}"));
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal($"line 1: {builtin}: {option}: option requires an argument\n{builtin}: usage: {builtin} {MapfileUsageArguments}\n",
            Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }

    [Theory]
    [InlineData("mapfile -n -1", "line 1: mapfile: -1: invalid line count\n")]
    [InlineData("readarray -O4294967296", "line 1: readarray: 4294967296: invalid array origin\n")]
    [InlineData("readarray -s-1", "line 1: readarray: -1: invalid line count\n")]
    [InlineData("mapfile -c0 -c2", "line 1: mapfile: 0: invalid callback quantum\n")]
    [InlineData("readarray -c4294967296", "line 1: readarray: 4294967296: invalid callback quantum\n")]
    [InlineData("mapfile -c --help", "line 1: mapfile: --help: invalid callback quantum\n")]
    public async Task Evaluation_MapfileBuiltin_RejectsInvalidNumbersBeforeReading(string command, string error)
    {
        var state = new Eval(Parser.Parse($"arr=(old); {command} arr"));
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal(error, Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }
}
