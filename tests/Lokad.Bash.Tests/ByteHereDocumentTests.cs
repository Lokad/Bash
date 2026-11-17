using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class ByteHereDocumentTests
{
    [Theory]
    [InlineData(false, "4CFF5C0A52", "4CFF520A")]
    [InlineData(true, "4CFF5C0A52", "4CFF520A")]
    [InlineData(false, "4C5C0D", "4C5C0D0A")]
    [InlineData(true, "4C5C0D", "4C5C0D0A")]
    [InlineData(false, "4C5C0A0D52", "4C0D520A")]
    [InlineData(true, "4C5C0A0D52", "4C0D520A")]
    public async Task ContinuationCollectionPreservesMalformedBytesAndCarriageReturns(
        bool prepared, string bodyHex, string outputHex)
    {
        byte[] source = [.. "cat <<END\n"u8, .. Convert.FromHexString(bodyHex), .. "\nEND\n"u8];
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var script = prepared ? Parser.Parse(source)
            : new ParsedScript([new ScriptCommand(ShellSource.FromBytes(source))]);
        var evaluation = ShellEvaluationStartup.CreateForNewHost(script, Evaluation.EvaluationOptions.Default);
        var result = await new Engine().RunAsync(evaluation, host, owner, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    public static IEnumerable<object[]> MalformedSubstitutions()
    {
        foreach (var body in new[] { "${v", "$(printf sub", "`printf sub", "\\`printf sub`", "\\\\\\`printf sub`", "$((1+2)", "$(if)" })
            foreach (var prepared in new[] { false, true })
                yield return [body, prepared];
    }

    [Theory]
    [MemberData(nameof(MalformedSubstitutions))]
    public async Task ReachedMalformedSubstitutionsFailSetupBeforeTheTargetRuns(string body, bool prepared)
    {
        var source = "target(){ printf forbidden; }\nprintf before\ntarget <<END\n" + body
            + "\nEND\nprintf ':after:%s' \"$?\"";
        var memory = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RunResult result;
        using (var owner = memory.CreateScope())
            result = prepared
                ? await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(source)), host, owner, deadline.Token)
                : await host.RunAsync(source, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:after:1", host.StandardOutput.TextContent);
        Assert.NotEmpty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("${v", false)]
    [InlineData("$(if)", false)]
    [InlineData("`printf sub", false)]
    [InlineData("$((1+2)", false)]
    [InlineData("${v", true)]
    [InlineData("$(if)", true)]
    [InlineData("`printf sub", true)]
    [InlineData("$((1+2)", true)]
    public async Task QuotedAndSkippedBodiesDoNotValidateSubstitutions(string body, bool quoted)
    {
        var source = quoted ? "cat <<'END'\n" + body + "\nEND\n"
            : "if false; then cat <<END\n" + body + "\nEND\nfi\nprintf after";
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await host.RunAsync(source, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(quoted ? body + "\n" : "after", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("2>&-")]
    [InlineData("2>err")]
    public async Task SubstitutionFailureUsesTheReachedDiagnosticDescriptor(string redirect)
    {
        var source = "target(){ printf forbidden; }\nprintf before\ntarget " + redirect + " <<END\n${v\nEND\nprintf ':after:%s' \"$?\"";
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await host.RunAsync(source, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:after:1", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        if (redirect == "2>err")
            Assert.NotEmpty(host.ReadFileBytes("/err"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ExpandedValuesRemainLiteralHereDocumentData()
    {
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await host.RunAsync("value='${v'; cat <<END\n$value\nEND\n", deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("${v\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierOperandEffectsSurviveFailedHereDocumentSetup(bool prepared)
    {
        const string Source = "value=old; i=0; printf before; read -r value <<END\n$((i++)) ${v\nEND\n"
            + "printf ':after:%s:%s:%s' \"$?\" \"$value\" \"$i\"";
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = prepared
            ? await new Engine().RunAsync(new Evaluation.Evaluation(Parser.Parse(Source)), host, deadline.Token)
            : await host.RunAsync(Source, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:after:1:old:1", host.StandardOutput.TextContent);
        Assert.NotEmpty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("v=set")]
    [InlineData("unset v")]
    public async Task NestedMalformedCommandSyntaxRejectsTheHereDocumentStructure(string initial)
    {
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await host.RunAsync(initial + "; cat <<END\n${v:-$(if)}\nEND\nprintf ':after:%s' \"$?\"", deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(":after:1", host.StandardOutput.TextContent);
        Assert.NotEmpty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(1, false, false, "5C220A")]
    [InlineData(2, false, false, "5C220A")]
    [InlineData(3, false, false, "5C5C220A")]
    [InlineData(4, false, false, "5C5C220A")]
    [InlineData(1, true, false, "5C220A")]
    [InlineData(2, true, false, "5C5C220A")]
    [InlineData(1, false, true, "5C220A")]
    [InlineData(2, true, true, "5C5C220A")]
    public async Task HereDocumentQuoteEscapesKeepTheirOwnContext(
        int backslashes, bool quoted, bool stripTabs, string outputHex)
    {
        var indent = stripTabs ? "\t" : "";
        var delimiter = quoted ? "'END'" : "END";
        var source = "cat " + (stripTabs ? "<<-" : "<<") + delimiter + "\n"
            + indent + new string('\\', backslashes) + "\"\n" + indent + "END\n";
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await host.RunAsync(source, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    // Bash 5.2.21/C.UTF-8: shared headers retain byte values and lexical expansion order.
    [Theory]
    [InlineData("x=$'\\377'; cat <<ONE; printf after\n$x\nONE\n", "FF0A6166746572", "")]
    [InlineData("x=$'\\377'; cat <<'ONE'; printf after\n$x\nONE\n", "24780A6166746572", "")]
    [InlineData("x=$'\\377'; cat <<ONE | cat\n$x\nONE\n", "FF0A", "")]
    [InlineData("x=$'\\377'; cat <<ONE | cat <<TWO\nignored\nONE\n$x\nTWO\n", "FF0A", "")]
    [InlineData("printf '\\u03bb'; cat <<ONE; printf '\\U0001f642'\nvalue\nONE\n", "CEBB76616C75650AF09F9982", "")]
    [InlineData("x=$'\\377'; false && cat <<ONE; printf done\n$x\nONE\n", "646F6E65", "")]
    [InlineData("cat <<ONE <<TWO; printf after\n$(printf first >&2; printf one)\nONE\n$(printf second >&2; printf two)\nTWO\n", "74776F0A6166746572", "66697273747365636F6E64")]
    [InlineData("cat <<''; printf after\nbody\n\n", "626F64790A6166746572", "")]
    [InlineData("cat <<''\n\nprintf after\n", "6166746572", "")]
    [InlineData("cat <<-'' | cat\n\tbody\r\n\t\nprintf after\n", "626F64790D0A6166746572", "")]
    public async Task Evaluation_SharedBodiesPreserveValuesAndEffects(string script, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("cat <<'ONE'; printf after\n", "\nONE\n", "FF0A6166746572")]
    [InlineData("cat <<ONE | cat <<'TWO'\nfirst\nONE\n", "\r\nTWO\nprintf after\n", "FF0D0A6166746572")]
    [InlineData("f(){ cat <<'END'; }; f\n", "\nEND\n", "FF0A")]
    [InlineData("x=$(cat <<ONE | cat\n", "\nONE\n); printf '%s' \"$x\"\n", "FF")]
    [InlineData("cat <<$'\\377'; printf after\nbody\n", "\n", "626F64790A6166746572")]
    [InlineData("cat <<-'END' | cat\n\t", "\r\n\tEND\n", "FF0D0A")]
    [InlineData("false && cat <<'END'; printf done\n", "\nEND\n", "646F6E65")]
    [InlineData("x=value; cat <<ONE | cat\n", "$x\nONE\n", "FF76616C75650A")]
    public async Task Evaluation_SharedBodiesRetainRawSourceBytes(string prefix, string suffix, string outputHex)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(source.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public void Parse_CompletedPipelineRetainsRawDelimiterAndBodySeparately()
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append("cat <<'"u8).Append(0xff).Append("' | cat\nbody\r\n"u8).Append(0xff).Append((byte)'\n');

        var pipeline = Assert.IsType<PipelineCommand>(Assert.Single(Parser.Parse(source.ToMemory()).Commands));
        var command = Assert.IsType<SimpleCommand>(pipeline.Left);
        var redirection = Assert.Single(command.Redirections);
        Assert.NotNull(redirection.HereDocumentDelimiter);
        var delimiter = Assert.Single(redirection.HereDocumentDelimiter.Parts);
        Assert.Equal(new byte[] { 0xff }, delimiter.GetUnexpandedValue().Bytes.ToArray());
        Assert.Equal(WordPartKind.SingleQuoted, delimiter.Kind);
        var body = Assert.Single(redirection.Target.Parts);
        Assert.Equal("body\r\n"u8.ToArray(), body.GetUnexpandedValue().Bytes.ToArray());
        Assert.Equal(WordPartKind.SingleQuoted, body.Kind);
    }

    // Display is a source contract, not executable Bash output; byte-valued argv cannot flatten to text.
    [Theory]
    [InlineData("cat <<END; printf '🙂'\nbodyλ\nEND", 0)]
    [InlineData("cat <<'λ' | cat\n🙂\nλ", 2)]
    [InlineData("cat <<END; printf $'\\377'\nλ\nEND", 0)]
    [InlineData("printf $'\\377' <<END\nλ\nEND", 0)]
    public void Display_SharedRegionsRetainUnicodeSourceBoundaries(string region, int stageCount)
    {
        var chain = ShellCommandParser.TryParseChain("printf 'pré'; " + region + "\nprintf after");

        Assert.NotNull(chain);
        Assert.Equal(new[] { "printf 'pré'", region, "printf after" }, chain.Select(segment => segment.Text));
        var stages = chain[1].Stages;
        if (stageCount == 0)
            Assert.Null(stages);
        else
        {
            Assert.NotNull(stages);
            Assert.Equal(stageCount, stages.Count);
        }
        Assert.NotEmpty(Parser.Parse(chain[1].Text).Commands);
    }
}
