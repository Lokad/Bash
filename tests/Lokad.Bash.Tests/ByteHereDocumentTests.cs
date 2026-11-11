using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteHereDocumentTests
{
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
