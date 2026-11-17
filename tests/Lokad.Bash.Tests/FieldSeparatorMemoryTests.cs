using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class FieldSeparatorMemoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualLengthIfsValuesAdmitMembershipAccordingToDiversity(bool distinct)
    {
        var varied = string.Concat(Enumerable.Range(0, 4096).Select(i => char.ConvertFromUtf32(0x100 + i)));
        var bytes = Encoding.UTF8.GetBytes(varied);
        if (!distinct) bytes.AsSpan().Fill((byte)'x');
        // Keep the original 50,176-byte probe separately: admitting even a small
        // index can cross MemoryCapacity's 64 KiB prepaid growth boundary there.
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var (state, variables) = Prepare(": $x", bytes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(state, host, owner, deadline.Token);

        Assert.Equal(distinct ? 1 : 0, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal(distinct, host.StandardError.TextContent.Contains("shared memory", StringComparison.Ordinal));
        Assert.Empty(host.CaptureEntries());
        var cache = variables.CachedFieldSeparators;
        Assert.Equal(distinct, CharacterIndex(cache) == null);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexRefusalPreservesPriorOutputAndSkipsTheUnreachedOpen(bool distinct)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 4096).Select(i => char.ConvertFromUtf32(0x100 + i))));
        if (!distinct) bytes.AsSpan().Fill((byte)'x');
        var governor = new MemoryGovernor(80 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var (state, _) = Prepare("printf before; : $x >effect", bytes);
        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);
        Assert.Equal(distinct ? 1 : 0, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal(distinct, host.StandardError.TextContent.Contains("shared memory", StringComparison.Ordinal));
        Assert.Equal(distinct ? 0 : 1, host.CaptureEntries().Count);
        if (!distinct) Assert.True(host.CaptureEntries().ContainsKey("effect"));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task CompletedScalarIndexCapacityStaysAdmittedUntilItsOwnerEnds()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 4096).Select(i => char.ConvertFromUtf32(0x100 + i))));
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var (state, variables) = Prepare(": $x", bytes);

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var cache = variables.CachedFieldSeparators;
        var characters = Assert.IsType<HashSet<Rune>>(CharacterIndex(cache));
        Assert.Equal(4096, characters.Count);
        var payload = IndexPayload(characters);
        Assert.True(payload > bytes.Length);
        Assert.True(variables.EstimatedMemoryBytes >= payload);
        Assert.True(governor.ReservedBytes >= payload);
        Assert.True(variables.TryUnset("IFS", UnsetFlags.None));
        Assert.True(variables.EstimatedMemoryBytes >= payload);
        Assert.True(cache.Contains(new Rune(0x100)));
        Assert.Empty(host.StandardError.Content.ToArray());
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ChildFirstMembershipDoesNotPublishIntoTheParentsCache()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 4096).Select(i => char.ConvertFromUtf32(0x100 + i))));
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var (state, variables) = Prepare(": $(: $x)", bytes);
        var parent = FieldSeparators.FromVariables(variables);

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Same(parent, variables.CachedFieldSeparators);
        Assert.Null(CharacterIndex(parent));
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void RefusalDoesNotPublishPartialMembershipIntoEitherClone()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(string.Concat(
            Enumerable.Range(0, 2048).Select(i => char.ConvertFromUtf32(0x100 + i))))));
        var original = FieldSeparators.FromVariables(variables);
        var child = variables.Clone();
        var governor = new MemoryGovernor(8192);
        var memory = new EvaluationMemory(governor);
        try
        {
            Assert.Throws<MemoryBudgetExceededException>(() =>
                original.PrepareCharacters(child, memory, CancellationToken.None));
            Assert.Same(original, variables.CachedFieldSeparators);
            Assert.Same(original, child.CachedFieldSeparators);
            Assert.Null(CharacterIndex(original));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterAdmissionDoesNotPublishAnIndex(bool duringGrowth)
    {
        var variables = Variables.CreateEmpty();
        var text = duringGrowth ? string.Concat(Enumerable.Range(0, 2048)
            .Select(i => char.ConvertFromUtf32(0x100 + i))) : "é:";
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(text)));
        var original = FieldSeparators.FromVariables(variables);
        var governor = new MemoryGovernor(64 * 1024);
        var admission = new CountingMemoryBudget(governor);
        var memory = new EvaluationMemory(admission);
        using var cancellation = new CancellationTokenSource();
        admission.AdmissionSucceeded += () =>
        {
            if (!duringGrowth || governor.ReservedBytes >= 4096) cancellation.Cancel();
        };
        try
        {
            var error = Assert.ThrowsAny<OperationCanceledException>(() =>
                original.PrepareCharacters(variables, memory, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Same(original, variables.CachedFieldSeparators);
            Assert.Null(CharacterIndex(original));
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(65_536)]
    [InlineData(262_144)]
    public void ByteMembershipUsesFixedStorageAndKeepsEveryInputByte(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(i => (byte)(i % 128 + 128)).ToArray();
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromBytes(bytes)));
        var original = FieldSeparators.FromVariables(variables);
        var governor = new MemoryGovernor(4096);
        var memory = new EvaluationMemory(governor);
        try
        {
            var prepared = original.PrepareBytes(variables, memory, CancellationToken.None);
            for (var value = 0; value < 256; value++)
                Assert.Equal(value >= 128, prepared.Contains((byte)value));
            Assert.InRange(prepared.EstimatedMemoryBytes - original.EstimatedMemoryBytes, 32, 512);
            Assert.Null(CharacterIndex(prepared));
            var reserved = governor.ReservedBytes;
            Assert.Same(prepared, prepared.PrepareBytes(variables, memory, CancellationToken.None));
            Assert.Equal(reserved, governor.ReservedBytes);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void CommandScopeAccountsItsReplacementWithoutGrowingTheBorrowedCache()
    {
        var caller = Variables.CreateEmpty();
        Assert.True(caller.TrySetScalar("IFS", ShellValue.FromText("é:")));
        var original = FieldSeparators.FromVariables(caller);
        var governor = new MemoryGovernor(64 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var command = Variables.CreateCommandScope(caller, memory, CancellationToken.None);
            var before = command.EstimatedMemoryBytes;
            var prepared = original.PrepareCharacters(command, memory, CancellationToken.None);
            Assert.Same(original, caller.CachedFieldSeparators);
            Assert.Null(CharacterIndex(original));
            Assert.Same(prepared, command.CachedFieldSeparators);
            Assert.True(command.EstimatedMemoryBytes >= before + prepared.EstimatedMemoryBytes);
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedMembershipStaysAdmittedThroughPendingHostWork(bool cancel)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 4096).Select(i => char.ConvertFromUtf32(0x100 + i))));
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var (state, variables) = Prepare(": $x; [[ -e /gate ]]; :", bytes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterGetPathInfo = async (path, _) =>
        {
            if (path.Path != "/gate") return;
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = new Engine().RunAsync(state, host, owner, deadline.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var characters = Assert.IsType<HashSet<Rune>>(CharacterIndex(variables.CachedFieldSeparators));
            Assert.Equal(4096, characters.Count);
            var payload = IndexPayload(characters);
            Assert.True(governor.ReservedBytes >= payload);
            if (cancel) deadline.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(governor.ReservedBytes >= payload);
        }
        finally
        {
            release.TrySetResult();
            try { await running; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        else
            Assert.Equal(0, (await running).ExitCode);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadRefusalPreservesTargetsAndTheNextUnreadRecord()
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(
            Enumerable.Range(0, 4096).Select(i => char.ConvertFromUtf32(0x100 + i))));
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        host.AddFile("/unit", "z\nnext\n");
        var (state, variables) = Prepare(
            "{ read -r a b; first=$?; IFS= read -r rest; second=$?; } <unit", bytes);
        Assert.True(variables.TrySetScalar("a", ShellValue.FromText("old-a")));
        Assert.True(variables.TrySetScalar("b", ShellValue.FromText("old-b")));
        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        foreach (var (name, expected) in new[] { ("a", "old-a"), ("b", "old-b"), ("rest", "next"), ("first", "1"), ("second", "0") })
        {
            Assert.True(variables.TryGetScalar(name, out var value), name + ": " + host.StandardError.TextContent);
            Assert.Equal(ShellValue.FromText(expected), value);
        }
        Assert.Contains("shared memory", host.StandardError.TextContent, StringComparison.Ordinal);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private static (Eval State, Variables Variables) Prepare(string script, byte[] separators)
    {
        var state = new Eval(Parser.Parse(script));
        var field = typeof(Eval).GetField("_vars", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException();
        var variables = Assert.IsType<Variables>(field.GetValue(state));
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromBytes(separators)));
        Assert.True(variables.TrySetScalar("x", ShellValue.FromText("z")));
        return (state, variables);
    }

    private static object? CharacterIndex(FieldSeparators separators) =>
        (typeof(FieldSeparators).GetField("_characters", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException()).GetValue(separators);

    private static long IndexPayload(HashSet<Rune> characters)
    {
        var entries = Assert.IsAssignableFrom<Array>(
            typeof(HashSet<Rune>).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(characters));
        var buckets = Assert.IsType<int[]>(
            typeof(HashSet<Rune>).GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(characters));
        var entryType = entries.GetType().GetElementType() ?? throw new InvalidOperationException();
        var entrySize = Assert.IsType<int>((typeof(FieldSeparatorMemoryTests)
            .GetMethod(nameof(ElementSize), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException()).MakeGenericMethod(entryType).Invoke(null, null));
        return (long)entries.Length * entrySize + sizeof(int) * (long)buckets.Length;
    }

    private static int ElementSize<T>() => Unsafe.SizeOf<T>();
}
