using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class DescriptorAllocationTests
{
    // Both Bash 5.2 pins retain the leading assignment and discard the redirection's writes.
    [Theory]
    [InlineData("fd=seed {fd}>out", "seed")]
    [InlineData("fd=seed; $missing {fd}>out", "seed")]
    [InlineData("{fd}>out 1>&\"$fd\"", "unset")]
    [InlineData("f(){ local -n fd=target; local target=seed; {fd}>out; printf '%s:' \"$target\"; }; f", "seed:unset")]
    public async Task CommandlessAllocationsStayVisibleOnlyDuringRedirectionSetup(string command, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(command + "; printf '%s' \"${fd-unset}\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.ReadFileBytes("/out"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("< input", "unset")]
    [InlineData("0<&\"0\"", "unset")]
    [InlineData("0>&-", "unset")]
    [InlineData("0<&0", "out")]
    [InlineData("3< input", "out")]
    [InlineData("<<< line", "out")]
    public async Task CommandlessStdinOperatorsSelectWhereTargetAssignmentsAreVisible(string redirection, string expected)
    {
        await using var host = new TestHost();
        host.AddFile("/input", "data");
        var result = await host.RunAsync(redirection + " > \"${target:=out}\"; printf '%s' \"$?\":\"${target-unset}\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0:" + expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.ReadFileBytes("/out"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("x=$(false) {fd}>out", 0, "0:unset", "")]
    [InlineData("x=$(false) >out", 0, "1:unset", "")]
    [InlineData("{fd}> \"${missing:?absent}\" || printf caught;", 0, "caught0:unset", "line 1: missing: absent\n")]
    [InlineData("set -e; {fd}> \"${missing:?absent}\"", 1, "", "line 1: missing: absent\n")]
    public async Task IsolatedCommandlessRedirectionsPublishTheirOwnStatus(
        string command, int expectedStatus, string expectedOutput, string expectedDiagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(command + "\nprintf '%s:%s' \"$?\" \"${fd-unset}\"", CancellationToken.None);

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, host.StandardOutput.TextContent);
        Assert.Equal(expectedDiagnostic, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task NamedAllocationRestoresAnEarlierNumericMoveInTheSameCommand()
    {
        const string script = """
            exec 7>original
            fd=7
            8>&7- {fd}>temporary
            printf kept >&"$fd"
            printf '%s' "$fd"
            """;
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("7", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("kept", host.ReadFile("/original"));
        Assert.Empty(host.ReadFileBytes("/temporary"));
        Assert.Equal(0, host.OpenFileCount);
    }

    // GNU Bash 5.2.37: allocation persists for builtins/functions; closing is temporary unless exec preserves it.
    [Theory]
    [InlineData("printf seed >out; : {fd}>>out; printf plus >&$fd; exec {fd}>&-", "", "seedplus")]
    [InlineData("printf 'a\\nbcd' >out; : {fd}<>out; IFS= read -r line <&$fd; printf XY >&$fd; IFS= read -r rest <&$fd; printf '%s:%s' \"$line\" \"$rest\"; exec {fd}>&-", "a:d", "a\nXYd")]
    [InlineData(": {fd}>|out; printf x >&$fd; printf '%s' \"$fd\"", "10", "x")]
    [InlineData("exec 3>out; : {fd}>&3; printf x >&$fd; printf '%s' \"$fd\"", "10", "x")]
    [InlineData("exec 3>out; : {fd}>&3-; printf x >&$fd; printf y >&3", "", "xy")]
    [InlineData("exec {fd}>out; : {fd}>&-; printf x >&$fd; printf '%s' \"$fd\"", "10", "x")]
    [InlineData("exec {fd}>out; word=-; : {fd}>&$word; printf x >&$fd", "", "x")]
    [InlineData("exec {fd}>out; readonly fd; : {fd}>&-; printf x >&$fd", "", "x")]
    [InlineData("exec {real}>out; declare -n fd=real; : {fd}>&-; printf x >&$real", "", "x")]
    [InlineData("declare -a arr; declare -n fd='arr[1]'; : {fd}>out; printf x >&\"${arr[1]}\"; printf '%s' \"${arr[1]}\"", "10", "x")]
    [InlineData("exec {real}>out; arr=([1]=$real); i=1; declare -n fd='arr[i++]'; : {fd}>&-; printf x >&$real; printf '%s' \"$i\"", "2", "x")]
    [InlineData("f(){ printf x >&$fd; printf '%s' \"$fd\"; }; fd=prefix f {fd}>>out; printf ':%s' \"${fd-unset}\"", "10:10", "x")]
    [InlineData("fd=global; f(){ local -n ref=fd; : {ref}>>out; printf x >&$fd; printf '%s' \"$fd\"; }; f; printf ':%s' \"$fd\"", "10:10", "x")]
    [InlineData("printf '<%s>' '{fd}'>out; printf '%s' \"${fd-unset}\"", "unset", "<{fd}>")]
    [InlineData("{ printf body; } {fd}>>out; printf x >&$fd", "body", "x")]
    public async Task AllocatedOperatorsShareFileModesAliasesAndBindingRules(string script, string output, string content)
    {
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await host.RunAsync(script, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(content, host.ReadFile("/out"));
    }

    [Theory]
    [InlineData(": {fd}<<<seed;")]
    [InlineData(": {fd}<<END\nseed\nEND\n")]
    [InlineData(": {fd}<<-END\n\tseed\n\tEND\n")]
    [InlineData("exec 3<<<seed; : {fd}<&3;")]
    [InlineData("exec 3<<<seed; : {fd}<&3-;")]
    public async Task AllocatedInputFormsUseTheOrdinaryReadAndClosePath(string setup)
    {
        await using var host = new TestHost();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await host.RunAsync(setup + " IFS= read -r line <&$fd; exec {fd}<&-; printf '%s:%s' \"$fd\" \"$line\"", deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("10:seed", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task EarlierTemporaryTargetStillRestoresTheOriginalMappingAfterAllocation()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("exec 10>old; : 10>&- {fd}>new; printf x >&$fd; printf '%s' \"$fd\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("10", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("x", host.ReadFile("/old"));
        Assert.Empty(host.ReadFileBytes("/new"));
    }

    [Theory]
    [InlineData("exec 3>out; exec {fd}>&3-; printf x >&$fd; printf y >&3", "x", "3: Bad file descriptor")]
    [InlineData("exec {fd}>out; exec {fd}>&-; printf x >&$fd", "", "Bad file descriptor")]
    [InlineData("exec {real}>out; arr=([1]=$real); declare -n fd='arr[1]'; exec {fd}>&-; printf x >&$real", "", "Bad file descriptor")]
    public async Task ExecPreservesAllocatedClosesAndSourceMoves(string script, string content, string diagnostic)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Contains(diagnostic, host.StandardError.TextContent);
        Assert.Equal(content, host.ReadFile("/out"));
    }

    [Theory]
    [InlineData("unset fd", "unset")]
    [InlineData("fd=", "")]
    [InlineData("fd=-1", "-1")]
    public async Task MissingEmptyAndNegativeCloseValuesFailWithoutRunningTheBody(string setup, string value)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(setup + "; printf wrong {fd}>&-; printf '%s:%s' \"$?\" \"${fd-unset}\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:" + value, host.StandardOutput.TextContent);
        Assert.Equal("fd: ambiguous redirect\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("1tail")]
    [InlineData("999999999999999999999")]
    [InlineData("4294967296")]
    [InlineData("-4294967296")]
    [InlineData("$'\\377'")]
    public async Task StoredCloseValuesUseSignedDecimalConversionWithoutDecodingBytes(string value)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"fd={value}; IFS= read -r line <<<seed {{fd}}>&-; printf '%s:%s' \"$?\" \"$line\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:", host.StandardOutput.TextContent);
        Assert.Contains("0: Bad file descriptor", host.StandardError.TextContent);
    }

    [Fact]
    public async Task CloseValueAllowsSignedDecimalWithSurroundingAsciiWhitespace()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("fd=$' \\t+1\\r\\v\\f '; printf wrong {fd}>&-; printf 'after:%s' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("after:1", host.StandardOutput.TextContent);
        Assert.Equal("printf: write error: Bad file descriptor\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("{fd}>>out")]
    [InlineData("{fd}>|out")]
    [InlineData("{fd}>&3")]
    public async Task ReadonlyAllocationOpensBeforeRejectingItsBindingAndStopsLaterRedirections(string redirection)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"exec 3>out; readonly fd=sentinel; printf wrong {redirection} >later; printf '%s:%s' \"$?\" \"$fd\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:sentinel", host.StandardOutput.TextContent);
        Assert.Equal("line 1: fd: readonly variable\nline 1: fd: cannot assign fd to variable\n", host.StandardError.TextContent);
        Assert.Empty(host.ReadFileBytes("/out"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/later"));
    }

    [Theory]
    [InlineData(": {fd}>\\\nopened", 3)]
    [InlineData(": {fd}>\"op\\\nened\"", 2)]
    [InlineData(": {fd}>>\\\nopened", 2)]
    [InlineData("{fd}>\"op\\\nened\" :", 3)]
    public async Task ReadonlyAllocationReportsBothNamesAtTheReachedSourceLine(string command, int line)
    {
        await using var host = new TestHost();
        host.AddFile("/body", ":\n" + command + " >later\n");
        var result = await host.RunAsync("readonly value=seed; declare -n fd=value\n"
            + "source /body 2>errors; printf '%s:%s' \"$?\" \"$fd\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:seed", host.StandardOutput.TextContent);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal($"/body: line {line}: value: readonly variable\n"
            + $"/body: line {line}: fd: cannot assign fd to variable\n", host.ReadFile("/errors"));
        Assert.Empty(host.ReadFileBytes("/opened"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/later"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("cat {fd}>out <<<seed; printf '%s' \"${fd-unset}\"", "seed\nunset")]
    [InlineData("declare -n fd=real; cat {fd}>out <<<seed; printf '%s' \"${real-unset}\"", "seed\nunset")]
    [InlineData("declare -a arr; i=0; declare -n fd='arr[i++]'; cat <<<\"${arr[0]:=old}\" {fd}>out <<<\"${arr[0]}:$i\"; printf '%s:%s' \"${arr[0]}\" \"$i\"", "10:1\n:0")]
    public async Task ExternalAllocationsShareOnlyTheirCommandExpansionBindings(string script, string output)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.ReadFileBytes("/out"));
    }

    [Fact]
    public async Task ReusedAllocationAndCloseSyntaxResolvesCurrentBindings()
    {
        var script = Parser.Parse("if [[ $1 == occupied ]]; then exec 10>/dev/null; fi; : {fd}>>out; printf '%s' \"$fd\"; printf x >&$fd; exec {fd}>&-");
        foreach (var occupied in new[] { false, true })
        {
            await using var host = new TestHost();
            var evaluation = new Evaluation.Evaluation(script);
            evaluation.ReplacePositionalParameters([ShellValue.FromText(occupied ? "occupied" : "free")]);
            var result = await new Engine().RunAsync(evaluation, host);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(occupied ? "11" : "10", host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.Equal("x", host.ReadFile("/out"));
        }
    }
}
