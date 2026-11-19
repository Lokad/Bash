using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationBraceExpansionTests
{
    [Theory]
    [InlineData("{a,{b,c}}{1..2}", "a1 a2 b1 b2 c1 c2")]
    [InlineData("{a,b}\"x\"{1..2}", "ax1 ax2 bx1 bx2")]
    [InlineData("'{1..1000000000}'", "{1..1000000000}")]
    [InlineData("{01..03}", "01 02 03")]
    [InlineData("{-02..01}", "-02 -01 000 001")]
    [InlineData("{-02..0001}", "-002 -001 0000 0001")]
    [InlineData("{+123..01..122}", "0123 0001")]
    [InlineData("{+01..+3}", "1 2 3")]
    [InlineData("{-00..2}", "000 001 002")]
    [InlineData("{001..7..-2}", "001 003 005 007")]
    [InlineData("{5..1..2}", "5 3 1")]
    [InlineData("{3..1..-0}", "3 2 1")]
    [InlineData("{1..3..}", "{1..3..}")]
    [InlineData("{1..3..9223372036854775808}", "{1..3..9223372036854775808}")]
    [InlineData("{1..3..-9223372036854775808}", "{1..3..-9223372036854775808}")]
    [InlineData("{3..1..-9223372036854775808}", "3")]
    [InlineData("{-9223372036854775808..-9223372036854775806}", "-9223372036854775808 -9223372036854775807 -9223372036854775806")]
    [InlineData("{9223372036854775806..9223372036854775807}", "9223372036854775806 9223372036854775807")]
    [InlineData("{09223372036854775806..9223372036854775807}", "-0000000000000000002 -0000000000000000001")]
    [InlineData("{-1..9223372036854775806..9223372036854775807}", "{-1..9223372036854775806..9223372036854775807}")]
    [InlineData("{-9223372036854775808..9223372036854775807..9223372036854775807}", "{-9223372036854775808..9223372036854775807..9223372036854775807}")]
    [InlineData("{2147483646..2147483647}", "2147483646 2147483647")]
    [InlineData("{-2147483647..-2147483648}", "-2147483647 -2147483648")]
    [InlineData("{2147483647..2147483647}", "2147483647")]
    [InlineData("{-2147483648..-2147483648}", "-2147483648")]
    [InlineData("{-2147483648..2147483647..2147483647}", "-2147483648 -1 2147483646")]
    [InlineData("{2147483647..-2147483648..-2147483648}", "2147483647 -1")]
    [InlineData("{a..z..2147483647}", "a")]
    [InlineData("{z..a..-2147483648}", "z")]
    public async Task ExpansionPreservesOrderQuotingAndIntegerEndpoints(string word, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("echo " + word);

        Assert.Equal(expected + "\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("{a..e..-2}", "<a><c><e>")]
    [InlineData("{e..a..2}", "<e><c><a>")]
    [InlineData("{a..1} {@..C} {é..ê}", "<{a..1}><{@..C}><{é..ê}>")]
    [InlineData("{Z..a}", "<Z><[><><]><^><_><`><a>")]
    [InlineData("{a..Z}", "<a><`><_><^><]><><[><Z>")]
    [InlineData("p{Y..a..3}$suffix", "<pYend><p$suffix><p_end>")]
    [InlineData("{,x} tail", "<x><tail>")]
    public async Task CharacterRangesPreserveExactFields(string words, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("suffix=end; printf '<%s>' " + words);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("pre{a,\"b c\"}post", "<preapost><preb cpost>")]
    [InlineData("{a,b\\,c}", "<a><b,c>")]
    [InlineData("{a,}", "<a>")]
    [InlineData("{a,\"\"}", "<a><>")]
    [InlineData("{a,${x:-{b,c}},z}", "<a><{b,c}><z>")]
    [InlineData("{a,\"$(printf '%s' 'b c')\"}", "<a><b c>")]
    [InlineData("x{a,{b,c}", "<x{a,b><x{a,c>")]
    [InlineData("{{a,b}}{1,2}", "<{a}1><{a}2><{b}1><{b}2>")]
    [InlineData("{{a,b}..c} {{a..b}..c}{x,y}", "<a..c><b..c><{{a..b}..c}x><{{a..b}..c}y>")]
    public async Task BraceGrammarTraversesOpaqueFragmentsAndInvalidParents(string source, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' " + source);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task BraceBranchesReuseQuotedSubstitutionsAndPreserveTheirEffects()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "f(){ printf '<%s>' pre{a,\"$(printf b; printf x >>effects)\"}{1,2}post; }; f; f",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Concat(Enumerable.Repeat("<prea1post><prea2post><preb1post><preb2post>", 2)),
            host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal("xxxx", host.ReadFile("/effects"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task DeepLiteralBraceParentsExposeTheInnerChoice()
    {
        var left = new string('{', 1024);
        var right = new string('}', 1024);
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' " + left + "{a,b}" + right);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<{left}a{right}><{left}b{right}>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task LiteralOuterSequenceDoesNotMaterializeItsInnerRange()
    {
        var governor = new MemoryGovernor(128 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync("printf '<%s>' {{1..1000000000}..z}{a,b}", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<{{1..1000000000}..z}a><{{1..1000000000}..z}b>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task FragmentProductIsAdmittedBeforeAnyBranchEffects()
    {
        var governor = new MemoryGovernor(128 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var word = string.Concat(Enumerable.Repeat("{a,\"b\"}", 12));
        var result = await host.RunAsync("printf prior\nprintf '<%s>' " + word +
            "$(printf reached >effect)\nprintf ':%s:after' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("prior:1:after", host.StandardOutput.TextContent);
        Assert.Equal("shell: shared memory budget exhausted\n", host.StandardError.TextContent);
        Assert.False(host.FileExists("/effect"));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task GeneratedBacktickWithUnquotedSuffixDiagnosesIncompleteSubstitution()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' prefix{Z..a}suffix");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("bad substitution: no closing \"`\" in `suffix\n", result.StdErr);
    }

    [Theory]
    [InlineData("""p{Y..a..3}"s" """, """<pYs><p"s><p_s>""")]
    [InlineData("""p{Y..a..3}"$v" """, """<pYone two><p"one><two><p_one two>""")]
    [InlineData("""p{Y..a..3}"x"$v""", """<pYxone><two><p"xone two><p_xone><two>""")]
    [InlineData("""p{Y..a..3}'$v'""", "<pY$v><p'one><two><p_$v>")]
    [InlineData("""p{Y..a..3}\$v""", """<pY$v><p\one><two><p_$v>""")]
    public async Task GeneratedEscapeChangesTheAdjacentQuoteContext(string word, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("v='one two'; printf '<%s>' " + word);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("\"s\"")]
    [InlineData("$(printf s)")]
    [InlineData("'s'")]
    public async Task GeneratedBacktickReadsAdjacentSyntaxInItsDiagnostic(string suffix)
    {
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' {Z..a}" + suffix);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("bad substitution: no closing \"`\" in `" + suffix + "\n", result.StdErr);
    }

    [Fact]
    public async Task GeneratedEscapeCanMakeAnAdjacentCommandSubstitutionLiteral()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "printf '<%s>' p{Y..a..3}$(printf ok; printf x >>effect)", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<pYok><p$(printf ok; printf x >> effect)><p_ok>", host.StandardOutput.TextContent);
        Assert.Equal("xx", host.ReadFile("/effect"));
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task AnEscapedSubstitutionShowsTheNormalizedCompoundSpelling()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' p{Y..a..3}$(case yes in yes) printf y;; esac)");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<pYy><p$(case yes in \n    yes)\n        printf y\n    ;;\nesac)><p_y>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("$( (printf yes) )", "yes")]
    [InlineData("$(f(){ printf yes; }; f)", "yes")]
    [InlineData("$(read -r var <<EOF\nyes\nEOF\nprintf '%s' \"$var\")", "yes")]
    public async Task ACommandSubstitutionStillRunsAfterItsOuterQuoteIsEscaped(string substitution, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' p{Y..a..3}\"" + substitution + "\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"<pY{expected}><p\"{expected}><p_{expected}>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task GeneratedBacktickFailureRetainsPrecedingBranchEffects()
    {
        var governor = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync(
            "printf '<%s>' p{Y..a..7}\"$(printf ok; printf x >>effect)\"\nprintf ':%s' \"$?\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(":1", host.StandardOutput.TextContent);
        Assert.Equal("x", host.ReadFile("/effect"));
        Assert.Equal("bad substitution: no closing \"`\" in `\"$(printf ok; printf x >> effect)\"\n",
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task GeneratedSubstitutionFailureDiscardsItsStreamingCommandUnit()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "printf prior >first\nprintf '%s' p{Z..a}s >body\nprintf tail >after", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("bad substitution: no closing \"`\" in `s\n", host.StandardError.TextContent);
        Assert.Equal("prior", host.ReadFile("/first"));
        Assert.Equal("tail", host.ReadFile("/after"));
        Assert.False(host.FileExists("/body"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task WideRangeRespectsMemoryAllowanceBeforePublishingFields()
    {
        var governor = new MemoryGovernor(128 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        var result = await host.RunAsync(
            "printf prior\nprintf '<%s>' {2147483648..2148483648}\nprintf ':%s:after' \"$?\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("prior:1:after", host.StandardOutput.TextContent);
        Assert.Equal("shell: shared memory budget exhausted\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task NumericPaddingCanExceedTheFormerStackBuffer()
    {
        var zeros = new string('0', 40);
        var result = await EvaluationTestHelper.RunAsync("echo {" + zeros + "1..2}");

        Assert.Equal(zeros + "1 " + zeros + "2\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RepeatedSingletonExpansionCannotExhaustTheStack()
    {
        var word = string.Concat(Enumerable.Repeat("{1..1}", 300));
        var result = await EvaluationTestHelper.RunAsync("echo " + word);

        Assert.Equal(new string('1', 300) + "\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void LiteralGroupsReleaseScanStorageBeforeReturningTheOriginalWord()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        using var retained = owner.Reserve(4096);
        var memory = new EvaluationMemory(owner);
        var word = new Word([WordPart.FromText(WordPartKind.Unquoted, string.Concat(Enumerable.Repeat("{x}", 128)))]);

        Assert.Same(word, Assert.Single(BraceExpansion.Expand(word, memory, CancellationToken.None)));
        Assert.True(governor.PeakBytes > 4096);
        Assert.Equal(4096, governor.ReservedBytes);
        Assert.Equal(0, memory.Workspace.Bytes);
    }

    [Fact]
    public void GeneratedSequenceWordsKeepTheirBytesAfterScratchIsReleased()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        using var retained = owner.Reserve(4096);
        var memory = new EvaluationMemory(owner);
        var word = new Word([WordPart.FromText(WordPartKind.Unquoted, "{01..64}")]);

        var expanded = BraceExpansion.Expand(word, memory, CancellationToken.None);

        Assert.Equal(64, expanded.Count);
        Assert.Equal("01", Assert.Single(expanded[0].Parts).GetUtf8Text());
        Assert.Equal("64", Assert.Single(expanded[^1].Parts).GetUtf8Text());
        Assert.True(governor.ReservedBytes > 4096);
        Assert.True(governor.ReservedBytes < governor.PeakBytes);
    }

    [Fact]
    public void RefusedScanReleasesItsOwnChargeBeforeCallerDisposal()
    {
        var governor = new MemoryGovernor(12288);
        using var owner = governor.CreateScope();
        using var retained = owner.Reserve(4096);
        var memory = new EvaluationMemory(owner);
        var word = new Word([WordPart.FromText(WordPartKind.Unquoted, string.Concat(Enumerable.Repeat("{x}", 128)))]);

        Assert.Throws<MemoryBudgetExceededException>(() => BraceExpansion.Expand(word, memory, CancellationToken.None));
        Assert.True(governor.PeakBytes > 4096);
        Assert.Equal(4096, governor.ReservedBytes);
    }

    [Fact]
    public void ExpansionHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BraceExpansion.Expand(new Word([WordPart.FromText(WordPartKind.Unquoted, "{1..1000}")]),
                new EvaluationMemory(new MemoryGovernor(TestMemoryBudget.EvaluationBytes)), cancellation.Token));
    }
}
