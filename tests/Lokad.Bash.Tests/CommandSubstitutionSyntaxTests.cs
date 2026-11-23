using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

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

    [Theory]
    [InlineData("v=$(for ((i=7)); do :; done)")]
    [InlineData("printf '%s' \"$(printf '%s' \"$(for ((i=7)); do :; done)\")\"")]
    [InlineData("v=\"${missing:-$(for ((i=7)); do :; done)}\"")]
    [InlineData("v=$((1 + $(for ((i=7)); do :; done)))")]
    [InlineData("[[ \"$(for ((i=7)); do :; done)\" == yes ]]")]
    [InlineData("v=$\\\n(for ((i=7)); do :; done)")]
    public void NestedGrammarFailurePreservesMessageAndSourceCoordinates(string command)
    {
        var source = "# é\n" + command;
        var expectedOffset = source.IndexOf("((i=7", StringComparison.Ordinal);
        Assert.False(Parser.TryParse(source, out _, out var textError));
        Assert.NotNull(textError);
        Assert.Equal("Arithmetic for header requires two semicolons.", textError.Message);
        Assert.Equal(expectedOffset, textError.Offset);

        Assert.False(Parser.TryParse(System.Text.Encoding.UTF8.GetBytes(source), out _, out var byteError));
        Assert.NotNull(byteError);
        Assert.Equal(textError.Message, byteError.Message);
        Assert.Equal(expectedOffset + 1, byteError.Offset);
    }

    [Theory]
    [InlineData("$(printf ok |)", (int)ShellScanStatus.Invalid, "Expected command after pipeline operator.")]
    [InlineData("$(printf ok |", (int)ShellScanStatus.Incomplete, null)]
    [InlineData("$(printf ')'", (int)ShellScanStatus.Incomplete, null)]
    public void FailedLookaheadReportsOnlyTheDiagnosedBodyError(string source, int expected, string? message)
    {
        var context = new ShellScanContext();
        Assert.Equal((ShellScanStatus)expected, CommandSubstitutionScanner.ScanParsedForLaterReuse(
            context, ShellSource.FromText(source), 0, out _, out var rejected, out var error));
        Assert.Null(rejected);
        Assert.Equal(message, error?.Error.Message);

        Assert.Equal(ShellScanStatus.Complete, CommandSubstitutionScanner.ScanParsed(
            context, ShellSource.FromText("$(printf valid)"), 0, out _, out var accepted, out var laterError));
        Assert.IsType<CommandSubstitutionSyntax.Parsed>(accepted);
        Assert.Null(laterError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HereDocumentExpansionReportsChildDiagnosticBeforeAnyCaptureEffects(bool prepared)
    {
        const string Source = """
            value=prior
            IFS= read -r value <<END
            $(: >inside; for ((i=7)); do : >body; break; done)
            END
            printf '%s:%s' "$?" "$value"
            """;
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new EndToEnd.TestHost { MemoryBudget = owner };
        var result = prepared
            ? await new Engine().RunAsync(new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(Source)),
                host, owner, CancellationToken.None)
            : await BashScript.RunAsync(host, Source, EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:prior", host.StandardOutput.TextContent);
        Assert.Equal("here-document: syntax error: Arithmetic for header requires two semicolons.\n",
            host.StandardError.TextContent);
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedHereDocumentSeparatorFailurePrecedesAllCaptureEffects(bool prepared)
    {
        const string source = """
            value=before
            value=$(printf prefix >trace
            { cat <<DOC
            body
            DOC
            }; # The original grammar can read the separator across this gap.
            printf tail)
            printf '<%s:%s>' "$?" "$value"
            """;
        await using var host = new EndToEnd.TestHost();
        var status = await RunAsync(source, host, prepared);

        Assert.Equal(0, status);
        Assert.Equal("<1:before>", host.StandardOutput.TextContent);
        Assert.Contains("here-document in command substitution", host.StandardError.TextContent);
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedHereDocumentCanJoinPipelineArgumentsAndKeepTheirLine(bool prepared)
    {
        const string source = """
            value=$({ : <<DOC
            body
            DOC
            } | printf '<%s>' "$LINENO"; printf tail)
            printf '[%s:%s]' "$?" "$value"
            """;
        await using var host = new EndToEnd.TestHost();
        var status = await RunAsync(source, host, prepared);

        Assert.Equal(0, status);
        Assert.Equal("[0:<7><printf><tail>]", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOmittedSeparatorStillCompletesPendingHereDocumentInput(bool prepared)
    {
        const string source = """
            value=$(cat <<DOC; printf '<%s>' "$LINENO";
            $LINENO
            DOC
            printf tail
            printf '[%s]' "$LINENO")
            printf '[%s:%s]' "$?" "$value"
            """;
        await using var host = new EndToEnd.TestHost();
        var status = await RunAsync(source, host, prepared);

        Assert.Equal(0, status);
        Assert.Equal("[0:5\n<8><printf><tail>[9]]", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AParameterOperandInAHereDocumentUsesItsSourceSeparators(bool prepared)
    {
        const string source = """
            cat <<END
            ${none:-$({ : <<DOC
            body
            DOC
            }; printf tail)}
            END
            """;
        await using var host = new EndToEnd.TestHost();
        var status = await RunAsync(source, host, prepared);

        Assert.Equal(0, status);
        Assert.Equal("tail\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public void ReplayedSeparatorRetainsNestedCaptureSyntaxFromTheSameSource()
    {
        const string input = """
            $({ : <<DOC
            body
            DOC
            } | printf '%s' "$(printf nested)"; printf tail)
            """;
        var source = ShellSource.FromText(input);
        var context = new ShellScanContext();
        var nestedStart = input.IndexOf("$(printf nested)", StringComparison.Ordinal);
        Assert.Equal(ShellScanStatus.Complete, CommandSubstitutionScanner.ScanParsedForLaterReuse(
            context, source, nestedStart, out _, out var cachedSubstitution, out _));
        Assert.Equal(ShellScanStatus.Complete, CommandSubstitutionScanner.ScanParsed(
            context, source, 0, out _, out var capture, out var error));
        Assert.Null(error);
        var script = Assert.IsType<CommandSubstitutionSyntax.Parsed>(capture);
        var pipeline = Assert.IsType<PipelineCommand>(Assert.Single(script.Script.Commands));
        var right = Assert.IsType<SimpleCommand>(pipeline.Right);
        Assert.Equal(5, right.Words.Count);
        var nested = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(right.Words[2].Parts));
        Assert.Same(cachedSubstitution, nested.Substitution);
    }

    private static async Task<int> RunAsync(string source, EndToEnd.TestHost host, bool prepared) => prepared
        ? (await EndToEnd.EndToEndHarness.RunScriptAsync(source, host)).ExitCode
        : (await BashScript.RunAsync(host, source, EvaluationOptions.Default, CancellationToken.None)).ExitCode;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdjacentSubshellReportsReachedCaptureFailureAfterEarlierCompleteUnits(bool prepared)
    {
        const string Source = """
            value=$((printf earlier); : >kept
            (: >discarded; for ((i=7)); do : >body; break; done); printf discarded)
            printf '%s:<%s>' "$?" "$value"
            """;
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new EndToEnd.TestHost { MemoryBudget = owner };
        var result = prepared
            ? await new Engine().RunAsync(new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(Source)),
                host, owner, CancellationToken.None)
            : await BashScript.RunAsync(host, Source, EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2:<earlier>", host.StandardOutput.TextContent);
        Assert.Equal("syntax error: Arithmetic for header requires two semicolons.\n", host.StandardError.TextContent);
        Assert.Equal("kept", Assert.Single(host.CaptureEntries()).Key);
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared)
            Assert.Equal(0, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task SkippedAdjacentSubshellDefersGrammarWhileFollowingCommandRuns()
    {
        var result = await EndToEnd.EvaluationTestHelper.RunAsync(
            "false && value=$((fi);); printf '%s' \"$?\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("value=$((fi);")]
    [InlineData("value=$( (fi);)")]
    public void DeferredAdjacentSubshellRequiresAnAdjacentOpenerAndOuterCloser(string source)
    {
        Assert.False(Parser.TryParse(source, out _, out _));
    }

    [Theory]
    [InlineData("\n", "for ((i=7)); do : >loop; break; done", "2:early/EXIT:2", true)]
    [InlineData("; ", "for ((i=7)); do : >loop; break; done", "2:/EXIT:2", false)]
    [InlineData("\n", "inner=$(for ((i=7)); do : >loop; break; done)", "1:early/EXIT:1", true)]
    [InlineData("\n", "inner=\"$(for ((i=7)); do : >loop; break; done)\"", "2:early/EXIT:2", true)]
    [InlineData("; ", "inner=\"$(for ((i=7)); do : >loop; break; done)\"", "0:early/EXIT:0", true)]
    public async Task FailedBacktickCaptureKeepsEarlierCompleteUnits(
        string separator, string malformed, string output, bool earlierUnit)
    {
        var script = "value=`trap 'printf \"/EXIT:%s\" \"$?\"' EXIT\nprintf early; : >early"
            + separator + malformed + "`\nprintf '%s:%s' \"$?\" \"$value\"";
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(256 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new EndToEnd.TestHost { MemoryBudget = owner };
            var result = prepared
                ? await new Engine().RunAsync(new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(script)),
                    host, owner, CancellationToken.None)
                : await BashScript.RunAsync(host, script, EvaluationOptions.Default, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Equal("syntax error: Arithmetic for header requires two semicolons.\n", host.StandardError.TextContent);
            Assert.Equal(earlierUnit ? new[] { "early" } : [], host.CaptureEntries().Keys);
            Assert.Equal(0, host.OpenFileCount);
            if (!prepared)
                Assert.Equal(0, governor.ReservedBytes);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Fact]
    public async Task BacktickExitInAnEarlierUnitSkipsSubsequentSyntaxFailure()
    {
        var result = await EndToEnd.EvaluationTestHelper.RunAsync(
            "value=`printf earlier\nexit 7\nfi`\nprintf '%s:%s' \"$?\" \"$value\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("7:earlier", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task FailedBacktickInHereDocumentKeepsCapturedBytes()
    {
        var result = await EndToEnd.EvaluationTestHelper.RunAsync("""
            IFS= read -r value <<END
            `printf earlier
            inner=$(for ((i=7)); do : >loop; break; done)`
            END
            printf '%s:%s' "$?" "$value"
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0:earlier", result.StdOut);
        Assert.Equal("syntax error: Arithmetic for header requires two semicolons.\n", result.StdErr);
        Assert.False(result.Host.FileExists("/loop"));
        Assert.Equal(0, result.Host.OpenFileCount);
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
            CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out var syntax, out _));
        return Assert.IsType<CommandSubstitutionSyntax.Legacy>(syntax);
    }

    [Fact]
    public void TokenizerConsumesTheLookaheadObjectWithoutKeepingItsCacheEntry()
    {
        var source = ShellSource.FromText("$(printf retained)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out var end, out var first, out _));
        var retained = Assert.IsType<CommandSubstitutionSyntax.Parsed>(first);
        Assert.Equal("printf retained", retained.Source.ToUtf8String());
        Assert.NotEmpty(retained.Script.Commands);
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out var lookedAgain, out _));
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
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out var end, out var first, out _));
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
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out _, out _));
        var depth = ShellScanContext.MaximumCommandDepth - 1;
        for (var level = 0; level < depth; level++)
            Assert.True(context.TryEnterCommand());
        try
        {
            Assert.False(context.TryGetCommandSubstitution(source, 0, out _));
            Assert.Equal(ShellScanStatus.Invalid,
                CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out _, out _));
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
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out var first, out _));
        var depth = 0;
        while (context.TryEnter()) depth++;
        try
        {
            Assert.Equal(ShellScanStatus.Invalid,
                CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out var rejected, out _));
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
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out _, out _));
        for (var depth = 0; depth < ShellScanContext.MaximumCommandDepth; depth++)
            Assert.True(context.TryEnterCommand());

        Assert.False(context.TryGetCommandSubstitution(source, 0, out _));
        Assert.Equal(ShellScanStatus.Invalid,
            CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out var rejected, out _));
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

    [Fact]
    public void LegacySyntaxKeepsItsExecutionTimeFailure()
    {
        const string source = "`if`";
        var word = Tokenizer.ReadExpansionWord(ShellSource.FromText(source), ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(word.Parts));
        var legacy = Assert.IsType<CommandSubstitutionSyntax.Legacy>(part.Substitution);
        Assert.Equal(source, legacy.Spelling.ToUtf8String());
        Assert.Equal("if", legacy.Source.ToUtf8String());
        Assert.Throws<SyntaxException>(() => Parser.Parse(legacy.Source.Bytes));
    }

    [Fact]
    public void DeferredHereDocumentCompletesWhenItsSourceIsRead()
    {
        var word = Tokenizer.ReadExpansionWord(ShellSource.FromText("$(cat <<END)"), ExpansionWordSyntax.Parameter,
            ShellQuoteKind.None);
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(word.Parts));
        var deferred = Assert.IsType<CommandSubstitutionSyntax.Deferred>(part.Substitution);
        Assert.Equal("cat <<END", deferred.Source.ToUtf8String());
        var script = Parser.Parse(deferred.Source.Bytes);
        var command = Assert.IsType<SimpleCommand>(Assert.Single(script.Commands));
        Assert.Empty(Assert.Single(Assert.Single(command.Redirections).Target.Parts).GetUtf8Text());
        var warning = Assert.Single(script.Locations?.GetHereDocumentWarnings(command) ?? []);
        Assert.Equal("END", warning.Delimiter.ToUtf8String());
    }

    private const string MatchingParenthesisEof = "unexpected EOF while looking for matching `)'\n";
    private const string MissingCaptureBodyOnLineTwo =
        "warning: here-document at line 2 delimited by end-of-file (wanted `END')\n";
    private const string ReadAheadWarning = "warning: command substitution: 1 unterminated here-document\n";

    [Theory]
    [InlineData("v=$(cat <<END); printf '<%s:%s>' \"$v\" \"$LINENO\"\nbody\nEND\nprintf '<%s>' \"$LINENO\"", "<body:3><4>", ReadAheadWarning, 0)]
    [InlineData("false && v=$(cat <<END)\nprintf tail\n", "", ReadAheadWarning
        + "warning: here-document at line 1 delimited by end-of-file (wanted `END')\n", 1)]
    [InlineData("false && v=$(cat <<END)\n$(printf forbidden >&2)\nEND\nprintf tail", "tail", ReadAheadWarning, 0)]
    [InlineData("exit 7\nv=$(cat <<END)\nbody\nEND", "", "", 7)]
    [InlineData("exit 7; v=$(cat <<END)\nbody\nEND", "", ReadAheadWarning, 7)]
    [InlineData("a=$(cat <<A) b=$(cat <<B); printf '<%s:%s>' \"$a\" \"$b\"\none\nA\ntwo\nB\nprintf tail", "<one:two>tail",
        ReadAheadWarning + ReadAheadWarning, 0)]
    [InlineData("v=$(cat <<FIRST <<SECOND); printf '<%s>' \"$v\"\none\nFIRST\ntwo\nSECOND", "<two>",
        "warning: command substitution: 2 unterminated here-documents\n", 0)]
    [InlineData("cat <<OUTER; v=$(cat <<INNER); printf '<%s>' \"$v\"\nin\nINNER\nout\nOUTER\nprintf tail", "out\n<in>tail",
        ReadAheadWarning, 0)]
    [InlineData("printf '<%s>' \"$(cat <<A)\"\none\nA\nprintf '<%s>' \"$(cat <<B)\"\ntwo\nB", "<one><two>",
        ReadAheadWarning + ReadAheadWarning, 0)]
    [InlineData("f() { printf '<%s>' \"$(cat <<END)\"; }\nbody\nEND\nf; f", "<body><body>", ReadAheadWarning, 0)]
    [InlineData("f() { printf '<%s>' \"$(cat <<END)\"; }; declare -f f\nbody\nEND",
        "f () \n{ \n    printf '<%s>' \"$(cat <<END\nbody\nEND\n)\"\n}\n", ReadAheadWarning, 0)]
    [InlineData("printf '<%s>' \"${missing:-$(cat <<END)\nbody\nEND\nsuffix}\"", "<body\nsuffix>", ReadAheadWarning, 0)]
    [InlineData("v=$((1 + $(cat <<END)\n4\nEND\n)); printf '<%s>' \"$v\"", "<5>", ReadAheadWarning, 0)]
    [InlineData("v=$(cat <<END) &\\\nbody\nEND\n& printf '<%s>' \"$v\"", "<body>", ReadAheadWarning, 0)]
    [InlineData("v=$((1 + $(cat <<END) )\\\n4\nEND\n); printf '<%s>' \"$v\"", "<5>", ReadAheadWarning, 0)]
    [InlineData("v=$(cat <<END); printf '<%s:%s>' \"$v\" 'head\nbody\nEND\nsuffix'", "<body:head\nsuffix>", ReadAheadWarning, 0)]
    [InlineData("cat <<OUTER\n<$(cat <<INNER)>\nbody\nINNER\nOUTER\nprintf tail", "tail", MatchingParenthesisEof, 0)]
    public async Task PendingCaptureBodiesAreCollectedWhileReadingTheHeader(
        string source, string output, string diagnostics, int exitCode)
    {
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            var result = prepared
                ? await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(source)), host, owner, CancellationToken.None)
                : await host.RunAsync(source, CancellationToken.None);

            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Equal(diagnostics, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Fact]
    public async Task CaptureReadAheadPreservesRawBodyBytes()
    {
        byte[] source = [.. "printf '<%s>' \"$(cat <<END)\"\n"u8, 0xff, 0x0d, .. "\nEND"u8];
        await using var host = new TestHost();
        var result = await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(source)), host, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { (byte)'<', 0xff, 0x0d, (byte)'>' }, host.StandardOutput.Content.ToArray());
        Assert.Equal(ReadAheadWarning, host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("printf prior\nv=$(cat <<END\nbody\n)\nprintf tail", "prior", MissingCaptureBodyOnLineTwo + MatchingParenthesisEof, 2)]
    [InlineData("printf prior; printf '%s' \"$(cat <<END\nbody\n)\"", "",
        "warning: here-document at line 1 delimited by end-of-file (wanted `END')\n" + MatchingParenthesisEof, 2)]
    [InlineData("exit 7\nv=$(cat <<END\nbody\n)", "", "", 7)]
    [InlineData("printf prior\nv=$(cat <<END", "prior", MissingCaptureBodyOnLineTwo + MatchingParenthesisEof, 2)]
    [InlineData("printf prior\nv=$(cat <<END\\\n", "prior", MatchingParenthesisEof, 2)]
    [InlineData("printf prior\nv=$(cat <<END\nbody\nEND\n", "prior", MatchingParenthesisEof, 2)]
    [InlineData("printf prior\nv=$(cat <<FIRST <<SECOND\nbody\nFIRST\nbody\n)", "prior",
        "warning: here-document at line 4 delimited by end-of-file (wanted `SECOND')\n" + MatchingParenthesisEof, 2)]
    [InlineData("printf prior\nv=${x:-$(printf '%s' \"$(cat <<END\nbody\n)\")}\n", "prior",
        MissingCaptureBodyOnLineTwo + MatchingParenthesisEof, 2)]
    [InlineData("printf prior\na=(\"$(cat <<END\nbody\n)\")", "prior", MissingCaptureBodyOnLineTwo + MatchingParenthesisEof, 1)]
    [InlineData("printf prior\neval 'v=$(cat <<END\nbody\n)' && printf unexpected; printf tail", "priortail",
        "warning: here-document at line 4 delimited by end-of-file (wanted `END')\n" + MatchingParenthesisEof, 0)]
    public async Task EndOfInputInHereDocumentCaptureReportsAtTheReachedReadBoundary(
        string source, string output, string diagnostics, int exitCode)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal(diagnostics, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task EndOfInputInConditionalCapturePreservesTheUnexpectedTokenByte()
    {
        var source = "printf prior\n[[ $(cat <<END\nbody\n) == body ]] && printf unexpected";
        await using var host = new TestHost();
        var result = await host.RunAsync(source, CancellationToken.None);

        byte[] diagnostic = [.. Utf8Text.Encode(MissingCaptureBodyOnLineTwo + MatchingParenthesisEof).Span,
            .. "unexpected token `"u8, 0xff, .. "' in conditional command\n"u8];
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("prior", host.StandardOutput.TextContent);
        Assert.Equal(diagnostic, host.StandardError.Content.ToArray());
    }

    [Fact]
    public void PreparedParserRejectsAnUnclosedHereDocumentCaptureAtTheInputEnd()
    {
        const string source = "printf prior\nv=$(cat <<END\nbody\n)\n";
        Assert.False(Parser.TryParse(source, out var script, out var error));
        Assert.Null(script);
        Assert.Equal(new SyntaxError(MatchingParenthesisEof.TrimEnd('\n'), source.Length), error);
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
