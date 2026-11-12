using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using System.Threading;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteSourceTests
{

    [Fact]
    public async Task TextConsumerFailure_UsesTheActiveErrorRedirectionOnce()
    {
        const string Script = "{ cd $'\\377' \"$(printf later >&2)\"; } 2>/errors; printf after";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal("shell: text value must be valid UTF-8\n",
            await EndToEndHarness.ReadFileAsync(result.Host, "/errors"));
    }

    [Theory]
    [InlineData("printf '<%s>' ", "\n", "3CFF3E")]
    [InlineData("printf '<%s>' '", "'\n", "3CFF3E")]
    [InlineData("printf '<%s>' \"", "\"\n", "3CFF3E")]
    [InlineData("printf '<%s>' \\", "\n", "3CFF3E")]
    [InlineData("printf '<%s>' $'", "'\n", "3CFF3E")]
    [InlineData("x=", "; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("a=([0]=", "); printf '<%s>' \"${a[0]}\"\n", "3CFF3E")]
    [InlineData("printf '<%s>' {one,", "}\n", "3C6F6E653E3CFF3E")]
    [InlineData("HOME=/base; printf '<%s>' ~/", "\n", "3C2F626173652FFF3E")]
    [InlineData("printf '<%s>' \"$(printf '%s' '", "')\"\n", "3CFF3E")]
    [InlineData("printf '<%s>' \"`printf '%s' '", "'`\"\n", "3CFF3E")]
    [InlineData("cat <<EOF\n", "\r\nEOF\n", "FF0D0A")]
    public async Task Evaluation_RetainsRawSourceBytes(string prefix, string suffix, string outputHex)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix));
        source.Append(0xff);
        source.Append(Utf8Text.Encode(suffix));
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(source.ToMemory()));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);
        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public void TokenRanges_UsePhysicalBytesAcrossUnicodeAndLookahead()
    {
        var source = ShellSource.FromText("é🙂 &\\\n& tail");
        var tokenizer = new Tokenizer(source);
        var word = tokenizer.NextToken();
        var separator = tokenizer.PeekToken();

        Assert.Equal("é🙂"u8.ToArray(), source.Bytes[word.Source].ToArray());
        Assert.Equal("&\\\n&"u8.ToArray(), source.Bytes[separator.Source].ToArray());
        Assert.Equal(separator.Source, tokenizer.NextToken().Source);
        Assert.Equal("tail"u8.ToArray(), source.Bytes[tokenizer.NextToken().Source].ToArray());
    }

    [Fact]
    public void SyntaxErrors_KeepEachPublicInputCoordinateSystem()
    {
        const string Script = "printf 'é🙂'; &";
        var source = ShellSource.FromText(Script);

        var textError = Assert.Throws<SyntaxException>(() => Parser.Parse(Script));
        var byteError = Assert.Throws<SyntaxException>(() => Parser.Parse(source.Bytes));

        Assert.Equal(Script.IndexOf('&'), textError.Offset);
        Assert.Equal(source.Length - 1, byteError.Offset);
        Assert.Equal(textError.Message, byteError.Message);
    }

    [Fact]
    public void DisplayChains_SliceUnicodeAtByteTokenBoundaries()
    {
        var chain = ShellCommandParser.TryParseChain("printf 'é🙂' && printf 'λ'");

        Assert.NotNull(chain);
        Assert.Equal(new[] { "printf 'é🙂'", "printf 'λ'" }, chain.Select(segment => segment.Text));
    }

    [Fact]
    public void HereDocumentDelimiter_CanContainNonUtf8Bytes()
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append("cat <<'"u8);
        source.Append(0xff);
        source.Append("'\nbody\r\n"u8);
        source.Append(0xff);
        source.Append((byte)'\n');

        var command = Assert.IsType<SimpleCommand>(Assert.Single(Parser.Parse(source.ToMemory()).Commands));
        var body = Assert.Single(Assert.Single(command.Redirections).Target.Parts);
        Assert.Equal("body\r\n"u8.ToArray(), body.GetUnexpandedValue().Bytes.ToArray());
        Assert.IsType<LiteralWordPart>(body);
    }
}
