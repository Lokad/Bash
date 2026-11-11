using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using System.Diagnostics.CodeAnalysis;
using Lokad.Bash.Memory;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class HostedFunctionExportTests
{
    [Theory]
    [InlineData(0, 17000)]
    [InlineData(55, 20000)]
    public async Task ExportLayoutDoesNotExhaustTheChildParsersTokenOrByteLimit(int depth, int commands)
    {
        // Pretty output adds one token per command and indentation per nested group.
        // Import the body without executing this many commands or changing parser limits.
        var definition = "f(){ " + string.Concat(Enumerable.Repeat("{ ", depth))
            + string.Concat(Enumerable.Repeat(":;", commands))
            + string.Concat(Enumerable.Repeat("};", depth)) + "}";
        var result = await EndToEndHarness.RunScriptAsync(
            definition + "; export -f f; sh -c 'declare -F f'", EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("f\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task ExportQuotingKeepsOneLiteralFromBecomingThousandsOfWordParts()
    {
        var definition = "f(){ : $'" + string.Concat(Enumerable.Repeat("\\'", 22000)) + "'; }";
        var result = await EndToEndHarness.RunScriptAsync(
            definition + "; export -f f; sh -c 'declare -F f'", EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("f\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task LongElifChainsRemainFlatAcrossExportAndImport()
    {
        var definition = "f(){ if false; then :; "
            + string.Concat(Enumerable.Repeat("elif false; then :; ", 64))
            + "else printf ok; fi; }";
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(definition).Commands));
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(function, output));
        var reparsed = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(output.ToMemory()).Commands));
        var conditional = Assert.IsType<IfCommand>(Assert.IsType<GroupCommand>(reparsed.Body).Body);
        Assert.Equal(64, conditional.ElifClauses.Count);

        var result = await EndToEndHarness.RunScriptAsync(definition + "; f; export -f f; sh -c f",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("okok", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public void CachedExportsRespectEachCallersBudgetAndSurviveAttributeCopies()
    {
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }").Commands));
        var binding = new FunctionBinding(definition, FunctionAttributes.Exported);
        var readonlyCopy = binding with { Attributes = FunctionAttributes.Exported | FunctionAttributes.Readonly };

        const long MemoryBudgetBytes = 4096;
        var memory = new MemoryGovernor(MemoryBudgetBytes);
        Assert.False(binding.TryGetExport(1, memory, out _));
        Assert.True(binding.TryGetExport(4096, memory, out var first));
        Assert.InRange(memory.ReservedBytes, first.Source.Length, memory.LimitBytes);
        using var pressure = memory.Reserve(memory.LimitBytes - memory.ReservedBytes);
        Assert.False(binding.TryGetExport(first.Source.Length - 1, memory, out _));
        Assert.True(readonlyCopy.TryGetExport(first.Source.Length, memory, out var second));
        Assert.True(first.Source.Equals(second.Source)); // Memory identity, not merely equal contents.
        var other = new FunctionBinding(definition, FunctionAttributes.Exported);
        Assert.Throws<MemoryBudgetExceededException>(() => other.TryGetExport(4096, memory, out _));
    }

    [Fact]
    public void UnpublishedExportRefundsItsReservationWhenAnotherReaderWins()
    {
        const int FunctionBodyBytes = 64 * 1024;
        const int ExportLimitBytes = 1024 * 1024;
        const long MemoryBudgetBytes = 4 * ExportLimitBytes;
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(
            Parser.Parse("f(){ : '" + new string('x', FunctionBodyBytes) + "'; }").Commands));
        var binding = new FunctionBinding(definition, FunctionAttributes.Exported);
        var memory = new MemoryGovernor(MemoryBudgetBytes);
        ExportedFunction published = default;
        long publishedBytes = 0;
        var competingReader = new ReservationCallbackBudget(memory, () =>
        {
            // Publish after the first reader misses the cache, before it finishes rendering.
            Assert.True(binding.TryGetExport(ExportLimitBytes, memory, out published));
            publishedBytes = memory.ReservedBytes;
        });

        Assert.True(binding.TryGetExport(ExportLimitBytes, competingReader, out var result));

        Assert.True(result.Source.Equals(published.Source));
        Assert.InRange(publishedBytes, published.Source.Length, memory.LimitBytes);
        Assert.Equal(publishedBytes, memory.ReservedBytes);
    }

    [Fact]
    public async Task HostCommandsReuseExportsUntilTheDefinitionChanges()
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(
            "f(){ :; }; export -f f; inspect; export -fn f; inspect; "
            + "export -f f; inspect; f(){ printf changed; }; inspect"));
        var host = new MockFileSystem();

        await new Engine().RunAsync(evaluation, host);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(4, host.CommandInvocations.Count);
        var original = Assert.Single(host.CommandInvocations[0].Functions).Source;
        Assert.Empty(host.CommandInvocations[1].Functions);
        Assert.True(original.Equals(Assert.Single(host.CommandInvocations[2].Functions).Source));
        var changed = Assert.Single(host.CommandInvocations[3].Functions).Source;
        Assert.False(original.Equals(changed));
        Assert.Contains("printf changed", Utf8Text.Decode(changed));
    }

    [Fact]
    public async Task ImportDoesNotRunBodyOrItsRedirections()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "f(){ printf wrong; } > /side-effect; export -f f; sh -c 'printf ready'",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/side-effect"));
    }

    [Fact]
    public async Task InvocationSeparatesFunctionsFromScalarsAndChildRetainsTheCompletedList()
    {
        var state = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(
            "f(){ :; }; f=value; export f; export -f f; inspect"));
        var host = new MockFileSystem();

        await new Engine().RunAsync(state, host);

        var invocation = Assert.Single(host.CommandInvocations);
        var exported = Assert.Single(invocation.Functions);
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(exported.Source).Commands));
        Assert.Equal(ShellValue.FromText("f"), definition.Name.Value);
        Assert.Contains(invocation.Environment, variable => variable.Name == "f" && variable.Value == ShellValue.FromText("value"));
        Assert.DoesNotContain(invocation.Environment, variable => variable.Name.ToValue().Bytes.Span.StartsWith("BASH_FUNC_"u8));
        var child = invocation.CreateChild("sh", [], [],
            invocation.StdIn, invocation.StdOut, invocation.StdErr);
        Assert.Same(invocation.Functions, child.Functions);
        Assert.Empty(child.Environment);
        Assert.Equal(invocation.CurrentDirectory, child.CurrentDirectory);
    }

    private sealed class ReservationCallbackBudget(IMemoryBudget budget, Action beforeFirstReservation) : IMemoryBudget
    {
        private bool _called;

        public IMemoryScope CreateScope() => budget.CreateScope();

        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!_called)
            {
                _called = true;
                beforeFirstReservation();
            }
            return budget.TryReserve(bytes, out reservation);
        }
    }
}
