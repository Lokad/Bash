using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class CommandSubstitutionSyntaxTests
{
    [Theory]
    [InlineData("value=$(time)")]
    [InlineData("value=\"$(false; time)\"")]
    [InlineData("value=$(! !)")]
    [InlineData("value=$(time\\\n)")]
    public void PipelinePrefixBeforeSubstitutionCloseRequiresATerminator(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Fact]
    public async Task ReachedLegacyTreesSharePhysicalSyntaxAndRefundAfterLastUser()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var allowance = governor.CreateScope();
        var owner = new SourceMemory(allowance.CreateScope());
        var source = ShellSource.FromText("`printf stable`");
        var legacy = ReadBackticks(source);
        var users = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            using var borrow = new SourceMemory.Borrow(owner);
            Assert.True(owner.TryPrepareLegacy(ReadBackticks(source), CancellationToken.None, out var result, out _));
            return result;
        })).ToArray();

        var prepared = await Task.WhenAll(users);
        Assert.All(prepared, tree => Assert.Same(prepared[0], tree));
        var retained = governor.ReservedBytes;
        Assert.True(retained > 0);
        Assert.True(owner.TryPrepareLegacy(legacy, CancellationToken.None, out var replay, out _));
        Assert.Same(prepared[0], replay);
        Assert.Equal(retained, governor.ReservedBytes);

        var independentlySpelled = ReadBackticks(ShellSource.FromText(source.ToUtf8String()));
        Assert.True(owner.TryPrepareLegacy(independentlySpelled, CancellationToken.None, out var different, out _));
        Assert.NotSame(replay, different);
        using (new SourceMemory.Borrow(owner))
        {
            owner.Release();
            Assert.True(governor.ReservedBytes > retained);
        }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void LegacyPrepareRefusalAndCancellationLeaveTheOwnerUsable()
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var allowance = governor.CreateScope();
        var owner = new SourceMemory(allowance.CreateScope());
        var legacy = ReadBackticks(ShellSource.FromText("`printf value`"));
        using (governor.Reserve(governor.LimitBytes - 256))
            Assert.Throws<MemoryBudgetExceededException>(() =>
                owner.TryPrepareLegacy(legacy, CancellationToken.None, out _, out _));
        Assert.Equal(0, governor.ReservedBytes);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            owner.TryPrepareLegacy(legacy, canceled.Token, out _, out _));
        Assert.Equal(0, governor.ReservedBytes);

        Assert.True(owner.TryPrepareLegacy(legacy, CancellationToken.None, out _, out _));
        Assert.True(governor.ReservedBytes > 0);
        owner.Release();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private static CommandSubstitutionSyntax.Legacy ReadBackticks(ShellSource source)
    {
        var context = new ShellScanContext { LegacyOrigin = LegacySubstitutionOrigin.Script };
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out var syntax));
        return Assert.IsType<CommandSubstitutionSyntax.Legacy>(syntax);
    }

    [Fact]
    public void TokenizerConsumesTheLookaheadObjectWithoutKeepingItsCacheEntry()
    {
        var source = ShellSource.FromText("$(printf retained)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out var end, out var first));
        var retained = Assert.IsType<CommandSubstitutionSyntax.Parsed>(first);
        Assert.Equal("printf retained", retained.Source.ToUtf8String());
        Assert.NotEmpty(retained.Script.Commands);
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out var lookedAgain));
        Assert.Same(first, lookedAgain);

        var tokenizer = new Tokenizer(source, 0, context);
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(token.Word.Parts));
        Assert.Same(first, part.Substitution);
        Assert.Equal(end + 1, tokenizer.Position);
        Assert.False(context.TryGetCommandSubstitution(source, 0, out _));
    }

    [Fact]
    public void LookaheadKeysUseSourceStorageAndPosition()
    {
        var source = ShellSource.FromText("$(printf one)$(printf two)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out var end, out var first));
        Assert.False(context.TryGetCommandSubstitution(ShellSource.FromText(source.ToUtf8String()), 0, out _));
        Assert.False(context.TryGetCommandSubstitution(source, end + 1, out _));
        Assert.True(context.TryTakeCommandSubstitution(source, 0, out var cached));
        Assert.Same(first, cached.Substitution);
        Assert.False(context.TryTakeCommandSubstitution(source, 0, out _));
    }

    [Fact]
    public void AdjacentSubshellLookaheadCanTransferToADeeperCommand()
    {
        var source = ShellSource.FromText("$((printf retained);)");
        var context = new ShellScanContext();
        var cursor = new ShellSourceCursor(source, 0, context);
        var selected = Assert.IsType<ShellNestedExpansionScan.Complete>(
            cursor.SkipNestedExpansion(BracedParameterRecognition.All));
        Assert.Equal(ShellNestedExpansionKind.Command, selected.Kind);
        Assert.True(context.TryGetCommandSubstitution(source, 0, out var cached));
        Assert.True(context.TryEnterCommand());
        try
        {
            var tokenizer = new Tokenizer(source, 0, context);
            Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
            var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(token.Word.Parts));
            Assert.Same(cached.Substitution, part.Substitution);
            Assert.Equal(source.Length, tokenizer.Position);
            Assert.False(context.TryGetCommandSubstitution(source, 0, out _));
        }
        finally { context.ExitCommand(); }
    }

    [Fact]
    public void TransferredLookaheadStillRequiresHeadroomForDescendantCommands()
    {
        var source = ShellSource.FromText("$( (printf retained) )");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out _));
        var depth = ShellScanContext.MaximumCommandDepth - 1;
        for (var level = 0; level < depth; level++)
            Assert.True(context.TryEnterCommand());
        try
        {
            Assert.False(context.TryGetCommandSubstitution(source, 0, out _));
            Assert.Equal(ShellScanStatus.Invalid,
                CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out _));
            Assert.Contains("Command nesting", Assert.IsType<SyntaxError>(context.LimitError).Message);
        }
        finally
        {
            for (var level = 0; level < depth; level++)
                context.ExitCommand();
        }
    }

    [Fact]
    public void NestedAdjacentSubshellsReuseLookaheadWithBoundedAllocation()
    {
        _ = Measure(2);
        var small = Measure(8);
        var large = Measure(24);
        Assert.True(large < 4 * small + 64 * 1024,
            $"Tripling the source depth allocated {small:N0} then {large:N0} bytes.");

        static long Measure(int depth)
        {
            var script = "leaf";
            for (var level = 0; level < depth; level++)
                script = "$((printf '%s' \"" + script + "\");)";
            var source = ShellSource.FromText("printf '%s' \"" + script + "\"");
            var before = GC.GetAllocatedBytesForCurrentThread();
            var parsed = Parser.Parse(source.Bytes);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Single(parsed.Commands);
            return allocated;
        }
    }

    [Fact]
    public void CachedBodiesDoNotBypassAnExhaustedNestingBudget()
    {
        var source = ShellSource.FromText("$(printf retained)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out var first));
        var depth = 0;
        while (context.TryEnter()) depth++;
        try
        {
            Assert.Equal(ShellScanStatus.Invalid,
                CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out var rejected));
            Assert.Null(rejected);
        }
        finally
        {
            for (var level = 0; level < depth; level++) context.Exit();
        }
        Assert.True(context.TryTakeCommandSubstitution(source, 0, out var cached));
        Assert.Same(first, cached.Substitution);
    }

    [Fact]
    public void CachedBodiesDoNotBypassCommandNestingLimits()
    {
        var source = ShellSource.FromText("$(printf retained)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out _));
        for (var depth = 0; depth < ShellScanContext.MaximumCommandDepth; depth++)
            Assert.True(context.TryEnterCommand());

        Assert.False(context.TryGetCommandSubstitution(source, 0, out _));
        Assert.Equal(ShellScanStatus.Invalid,
            CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out var rejected));
        Assert.Null(rejected);
        var error = Assert.IsType<SyntaxError>(context.LimitError);
        Assert.Contains("Command nesting", error.Message);
    }

    [Fact]
    public void RetainedTreeContainsCompletedHereDocumentBodies()
    {
        var word = Tokenizer.ReadExpansionWord(ShellSource.FromText("$(cat <<'END'\nbody\nEND\n)"), ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(word.Parts));
        var parsed = Assert.IsType<CommandSubstitutionSyntax.Parsed>(part.Substitution);
        var command = Assert.IsType<SimpleCommand>(Assert.Single(parsed.Script.Commands));
        var redirection = Assert.Single(command.Redirections);
        Assert.Equal("body\n", Assert.Single(redirection.Target.Parts).GetUtf8Text());
        Assert.NotNull(redirection.HereDocumentDelimiter);
        Assert.Equal("END", Assert.Single(redirection.HereDocumentDelimiter.Parts).GetUtf8Text());
    }

    [Theory]
    [InlineData("`if`", "if")]
    [InlineData("$(cat <<END)", "cat <<END")]
    public void DeferredSyntaxKeepsItsExecutionTimeFailure(string source, string payload)
    {
        var word = Tokenizer.ReadExpansionWord(ShellSource.FromText(source), ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(word.Parts));
        var deferred = part.Substitution;
        if (deferred is CommandSubstitutionSyntax.Legacy legacy)
            Assert.Equal(source, legacy.Spelling.ToUtf8String());
        else
            Assert.IsType<CommandSubstitutionSyntax.Deferred>(deferred);
        Assert.Equal(payload, deferred.Source.ToUtf8String());
        Assert.Throws<SyntaxException>(() => Parser.Parse(deferred.Source.Bytes));
    }

    [Fact]
    public void RetainedBodyPreservesInvalidUtf8LiteralBytes()
    {
        var source = ShellSource.FromBytes("$(printf '"u8.ToArray().Concat(new byte[] { 0xff }).Concat("')"u8.ToArray()).ToArray());
        var word = Tokenizer.ReadExpansionWord(source, ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(word.Parts));
        var parsed = Assert.IsType<CommandSubstitutionSyntax.Parsed>(part.Substitution);
        var command = Assert.IsType<SimpleCommand>(Assert.Single(parsed.Script.Commands));
        Assert.Equal(new byte[] { 0xff }, Assert.Single(command.Words[1].Parts).GetUnexpandedValue().Bytes.ToArray());
        Assert.True(parsed.Source.Bytes.Equals(source.Bytes.Slice(2, source.Length - 3)));
    }
}
