using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("cmd 1</dev/null", FileOpenMode.Read)]
    [InlineData("cmd >/dev/null", FileOpenMode.WriteTruncate)]
    [InlineData("cmd >>/dev/null", FileOpenMode.WriteAppend)]
    [InlineData("cmd 1<>/dev/null", FileOpenMode.ReadWrite)]
    [InlineData("exec 3</dev/null; cmd >&3", FileOpenMode.Read)]
    [InlineData("exec 3</dev/null; exec 4<&3 3<&-; cmd >&4", FileOpenMode.Read)]
    [InlineData("exec 3>/dev/null; exec 4>&3 3>&-; cmd >&4", FileOpenMode.WriteTruncate)]
    [InlineData(": {fd}>/dev/null; cmd >&$fd", FileOpenMode.WriteTruncate)]
    [InlineData(": {fd}</dev/null; cmd >&$fd", FileOpenMode.Read)]
    public async Task Evaluation_NullDevice_PreservesAccessWithoutOpeningOrOwningResources(
        string script, FileOpenMode mode)
    {
        var state = new Eval(Parser.Parse(Utf8(script)));

        var run = await NextCommandAsync(state, "cmd");
        var invocation = run.Command;
        Assert.Equal(FileDescriptor.ForDevNull(mode), invocation.StdOut);
        Assert.Empty(invocation.DescriptorsToClose);
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }

    [Fact]
    public async Task Evaluation_NullDevice_CombinedOutputSharesOneWriteOnlyMapping()
    {
        var state = new Eval(Parser.Parse(Utf8("cmd &>/dev/null")));

        var run = await NextCommandAsync(state, "cmd");
        var invocation = run.Command;
        Assert.Equal(FileDescriptor.ForDevNull(FileOpenMode.WriteTruncate), invocation.StdOut);
        Assert.Equal(invocation.StdOut, invocation.StdErr);
        Assert.Empty(invocation.DescriptorsToClose);
        run.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }
}
