using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData(8, false)]
    [InlineData(2048, true)]
    public async Task ForItems_AdmitLiteralBackingBeforeAssigningOrExecutingTheBody(int count, bool refused)
    {
        var items = string.Join(' ', Enumerable.Repeat("'value'", count));
        var state = new Eval(Parser.Parse($"item=before; for item in {items}; do reached=yes; break; done"));
        var budget = new MemoryGovernor(16 * 1024);
        using var owner = budget.CreateScope();
        var host = new TestOperationVisitor([]) { MemoryBudget = owner, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(refused ? 1 : 0, state.LastExitCode);
        Assert.Equal(refused ? "before" : "value", GetVariable(state, "item"));
        Assert.Equal(!refused, state.Variables.ContainsKey("reached"));
        if (refused)
        {
            var diagnostic = Assert.Single(host.Appends);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
            Assert.Equal("shell: shared memory budget exhausted\n", Text(diagnostic.Content));
        }
        else
            Assert.Empty(host.Appends);
    }

    [Fact]
    public async Task ForItems_KeepTheVectorChargedThroughTheBodyAndRefundItOnBreak()
    {
        const int Narrow = 8;
        const int Wide = 2048;
        var narrow = await ObserveWorkspaceAsync(Narrow);
        var wide = await ObserveWorkspaceAsync(Wide);
        var extraBacking = MemoryCapacity.ArrayBackingBytes<ShellValue>(Wide)
            - MemoryCapacity.ArrayBackingBytes<ShellValue>(Narrow);
        Assert.True(wide.WhilePending - narrow.WhilePending >= extraBacking,
            $"Expected at least {extraBacking} extra bytes during the wide body. Narrow: {narrow}, wide: {wide}.");
        Assert.Equal(narrow.AfterBreak, wide.AfterBreak);

        static async Task<(long WhilePending, long AfterBreak)> ObserveWorkspaceAsync(int count)
        {
            var items = string.Join(' ', Enumerable.Repeat("'value'", count));
            var state = new Eval(Parser.Parse($"for item in {items}; do test -e body; break; done; test -e after"));
            var budget = new MemoryGovernor(128 * 1024);
            using var owner = budget.CreateScope();
            state.Memory.Budget = owner;

            var body = await NextPathKindsAsync(state);
            Assert.Equal("/body", Assert.Single(body.Paths).Path);
            var whilePending = state.Memory.Workspace.Bytes;
            body.SetResult([PathKindResult(FilePathKind.File)]);

            var after = await NextPathKindsAsync(state);
            Assert.Equal("/after", Assert.Single(after.Paths).Path);
            var afterBreak = state.Memory.Workspace.Bytes;
            after.SetResult([PathKindResult(FilePathKind.File)]);

            Assert.Null(await NextStepAsync(state));
            Assert.Equal(0, state.LastExitCode);
            return (whilePending, afterBreak);
        }
    }

    [Fact]
    public async Task ForItems_ReleaseTheVectorBeforeTheFinalBody()
    {
        const int Narrow = 8;
        const int Wide = 8192;
        var narrow = await ObserveAsync(Narrow);
        var wide = await ObserveAsync(Wide);
        var extraBacking = MemoryCapacity.ArrayBackingBytes<ShellValue>(Wide)
            - MemoryCapacity.ArrayBackingBytes<ShellValue>(Narrow);
        Assert.Equal(extraBacking, wide.First.Live - narrow.First.Live);
        Assert.Equal(narrow.Final.Live, wide.Final.Live);
        Assert.True(wide.First.Reserved - wide.Final.Reserved >= extraBacking,
            $"Expected the wide loop to refund its extra backing before the final body. First: {wide.First}, final: {wide.Final}.");

        static async Task<((long Live, long Reserved) First, (long Live, long Reserved) Final)> ObserveAsync(int count)
        {
            var middle = string.Join(' ', Enumerable.Repeat("'other'", count - 2));
            var state = new Eval(Parser.Parse(
                $"for item in 'begin' {middle} 'final'; do case \"$item\" in begin|final) test -e \"$item\";; esac; done"));
            var budget = new MemoryGovernor(512 * 1024);
            using var owner = budget.CreateScope();
            state.Memory.Budget = owner;

            var first = await NextPathKindsAsync(state);
            Assert.Equal("/begin", Assert.Single(first.Paths).Path);
            var firstUsage = (state.Memory.Workspace.Bytes, budget.ReservedBytes);
            first.SetResult([PathKindResult(FilePathKind.File)]);

            var final = await NextPathKindsAsync(state);
            Assert.Equal("/final", Assert.Single(final.Paths).Path);
            var finalUsage = (state.Memory.Workspace.Bytes, budget.ReservedBytes);
            final.SetResult([PathKindResult(FilePathKind.File)]);

            Assert.Null(await NextStepAsync(state));
            Assert.Equal(0, state.LastExitCode);
            Assert.Equal("final", GetVariable(state, "item"));
            owner.Dispose();
            Assert.Equal(0, budget.ReservedBytes);
            return (firstUsage, finalUsage);
        }
    }

    [Fact]
    public async Task QuotedPositionalItems_KeepNoncompactValuesAfterTheFrameIsCleared()
    {
        var state = new Eval(Parser.Parse("for item in \"${@}\"; do set --; test -e body; done"));
        var backing = new byte[4096];
        backing[123] = 0xFF;
        state.ReplacePositionalParameters([ShellValue.FromText("first"), ShellValue.FromBytes(backing.AsMemory(123, 1))]);
        var budget = new MemoryGovernor(128 * 1024);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;

        var first = await NextPathKindsAsync(state);
        Assert.Equal("first", GetVariable(state, "item"));
        Assert.Empty(state.PositionalParameters);
        first.SetResult([PathKindResult(FilePathKind.File)]);

        var second = await NextPathKindsAsync(state);
        var item = state.Variables["item"];
        Assert.Equal(new byte[] { 0xFF }, item.Bytes.ToArray());
        Assert.True(item.TryGetCompactArrayCapacity(out var capacity));
        Assert.Equal(1, capacity);
        second.SetResult([PathKindResult(FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }
}
