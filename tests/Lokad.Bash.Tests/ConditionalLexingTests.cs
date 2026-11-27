using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed partial class ConditionalLexingTests
{
    [Theory]
    [InlineData("$'body\\nsuffix' == $'body\\nsuffix'", "'body\nsuffix' == 'body\nsuffix'")]
    [InlineData("-z $'\\0tail'", "-z ''")]
    [InlineData("$'\\x27' == $'\\x27'", "\\' == \\'")]
    [InlineData("'a.b' =~ ^$'a.b'$", "'a.b' =~ ^'a.b'$")]
    [InlineData("$\\\n'hello\\nworld' == $'hello\\nworld'", "'hello\nworld' == 'hello\nworld'")]
    [InlineData("\"$'marked\\ntext'\" == \"$'marked\\ntext'\"", "\"$'marked\\ntext'\" == \"$'marked\\ntext'\"")]
    [InlineData("\"$(printf %s $'a\\nb')\" == $'a\\nb'", "\"$(printf %s 'a\nb')\" == 'a\nb'")]
    [InlineData("! ! (yes&&yes) || no", "( -n yes && -n yes ) || -n no")]
    [InlineData("yes && # comment\n yes", "-n yes && -n yes")]
    [InlineData("$\\\n\"yes\" == yes", "\"yes\" == yes")]
    [InlineData("! ! ! \"\"", "! -n \"\"")]
    [InlineData("\"$([[ x ]]; printf a)$([[ $\"y\" == y ]] && printf b)\" == ab",
        "\"$([[ -n x ]]; printf a)$([[ \"y\" == y ]] && printf b)\" == ab")]
    [InlineData("${missing:-$'a\\nb'} == $'a\\nb'", "${missing:-'a\nb'} == 'a\nb'")]
    [InlineData("\"${missing:-$'a\\nb'}\" == $'a\\nb'", "\"${missing:-a\nb}\" == 'a\nb'")]
    [InlineData("\"${missing:-$'$(printf value)'}\" == value", "\"${missing:-$(printf value)}\" == value")]
    [InlineData("\"${missing:-$([[ value ]]; printf value)}\"", "-n \"${missing:-$([[ -n value ]]; printf value)}\"")]
    public async Task ConditionalDisplayRendersDecodedOperandsAndKeepsTheirMeaning(string condition, string spelling)
    {
        var script = "f(){ [[ " + condition + " ]] && printf hit; }; declare -f f; d=$(declare -f f); unset -f f; eval \"$d\"; f";
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
                : await host.RunAsync(script, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("f () \n{ \n    [[ " + spelling + " ]] && printf hit\n}\nhit", host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("\"${v:-$'a\\0hidden'}\"\"$(printf dropped)\"$'tail' == a || x == x", "", "\"${v:-a == a || x == x", "")]
    [InlineData("\"${v:-$'a\\0hidden'}\" == \"${v:-$'a}\\0hidden'}\"", "", "\"${v:-a == \"${v:-a}", "")]
    [InlineData("x == x || \"${v:-$'a\\0hidden'}\" == a", "; f; printf '[%s]' \"$?\"", "x == x || \"${v:-a == a", "[0]")]
    public async Task ConditionalDisplayEndsTheOperandAtDecodedNul(string condition, string invocation, string spelling, string output)
    {
        var script = "unset v\nf(){ [[ " + condition + " ]]; }; declare -f f" + invocation;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var exitCode = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, exitCode);
            Assert.Equal("f () \n{ \n    [[ " + spelling + " ]]\n}\n" + output, host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task ExportedConditionalKeepsItsCanonicalSpellingAndProtectedData()
    {
        const string script = "f(){ [[ \"$([[ yes ]]; printf yes)\" == $\"yes\" && (! \"\") ]]; }; "
            + "export -f f; bash -c 'declare -f f; f; printf \"[%s]\" \"$?\"'";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("f () \n{ \n    [[ \"$([[ -n yes ]]; printf yes)\" == \"yes\" && ( ! -n \"\" ) ]]\n}\n[0]",
            result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task ConditionalDisplayKeepsMalformedUtf8AndCarriageReturnAsBytes()
    {
        const string script = "f(){ [[ -n $'\\377\\r' ]]; }; type f";
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        byte[] expected = [.. "f is a function\nf () \n{ \n    [[ -n '"u8, 0xff, 0x0d, .. "' ]]\n}\n"u8];
        Assert.Equal(expected, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task ConditionalLexicalRolesPreserveNativeBytesAndCompleteEffects(
        string name, string script, int status, string outputHex, bool diagnostic,
        string filesJson, string entriesJson, bool invalidSyntax, bool prepared)
    {
        Assert.NotEmpty(name);
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new ExportTestHost { MemoryBudget = owner };
        using var files = JsonDocument.Parse(filesJson);
        foreach (var file in files.RootElement.EnumerateObject())
            host.AddFile("/" + file.Name, file.Value.GetString() ?? throw new InvalidDataException());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (prepared && invalidSyntax)
        {
            // Complete preparation rejects invalid root syntax before any evaluation.
            Assert.Throws<SyntaxException>(() => Parser.Parse(script));
            Assert.Empty(host.StandardOutput.Content.ToArray());
            Assert.Empty(host.StandardError.Content.ToArray());
        }
        else
        {
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, deadline.Token)
                : await BashScript.RunAsync(host, script, EvaluationOptions.Default, deadline.Token);
            Assert.Equal(status, result.ExitCode);
            Assert.Equal(outputHex, Convert.ToHexString(host.StandardOutput.Content.Span));
            Assert.Equal(diagnostic, !host.StandardError.Content.IsEmpty);
        }
        using var expected = JsonDocument.Parse(entriesJson);
        var entries = host.CaptureEntries();
        Assert.Equal(expected.RootElement.EnumerateObject().Count(), entries.Count);
        foreach (var entry in expected.RootElement.EnumerateObject())
        {
            Assert.True(entries.TryGetValue(entry.Name, out var actual), entry.Name);
            Assert.Equal(FilePathKind.File, actual.Kind);
            Assert.Equal(entry.Value.GetString(), "File:" + actual.ContentHex);
        }
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared) Assert.Equal(0, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("[[ '#' == # ]] && : >effect")]
    [InlineData("[[ x == # ]] fake close\n ]] && : >effect")]
    [InlineData("[[ x == x # inert \\\n&& ]] && : >effect")]
    [InlineData("[[ x || -n > ]] && : >effect")]
    [InlineData("[[ x =~ (a ]] && : >effect")]
    public async Task MissingOperandsCannotExecuteEarlierLookingClosingDelimiters(string invalid)
    {
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var source = ": >before\n" + invalid + "\n: >after";
        Assert.False(Parser.TryParse(source, out _, out _));
        var result = await host.RunAsync(source, CancellationToken.None);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.NotEmpty(host.StandardError.Content.ToArray());
        var entry = Assert.Single(host.CaptureEntries());
        Assert.Equal("before", entry.Key);
        Assert.Equal((FilePathKind.File, ""), entry.Value);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedConditionalSyntaxFitsOneSharedAllowanceWithoutExecutingTheSkippedTree(bool prepared)
    {
        var expression = "printf x >forbidden";
        for (var index = 0; index < 8; index++) expression = "[[ $(" + expression + ") ]]";
        var source = "false && " + expression + "; printf tail";
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        using var retained = owner.Reserve(16 * 1024);
        await using var host = new TestHost { MemoryBudget = owner };
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, CancellationToken.None)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared) Assert.Equal(16 * 1024, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedConditionalSyntaxPreservesEarlierCommandUnitsAndRefundsNormalizationScratch(bool comments)
    {
        var expression = string.Join(comments ? " # inert ]] $( '\n&& " : " && ", Enumerable.Repeat("x == x", 800));
        var governor = new MemoryGovernor(48 * 1024);
        using var owner = governor.CreateScope();
        using var retained = owner.Reserve(8 * 1024);
        await using var host = new TestHost { MemoryBudget = owner };
        var result = await host.RunAsync(": >before\n[[ " + expression + " ]] && : >forbidden\n: >after", CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("memory", host.StandardError.TextContent, StringComparison.OrdinalIgnoreCase);
        var entry = Assert.Single(host.CaptureEntries());
        Assert.Equal("before", entry.Key);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(8 * 1024, governor.ReservedBytes);
        retained.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
