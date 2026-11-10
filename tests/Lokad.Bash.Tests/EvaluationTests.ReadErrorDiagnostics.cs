using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    // Bash 5.2.37 (C.UTF-8); diagnostics omit the native process/line prefix.
    [Theory]
    [InlineData("value=old; read value 0>/dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("value=old; exec 3>/dev/null; read -u3 value; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 3: Bad file descriptor\n")]
    [InlineData("value=old; exec 3>/dev/null; exec 8>&3; read -u8 value; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 8: Bad file descriptor\n")]
    [InlineData("value=old; exec 3>/dev/null; read -u '  +003 ' value; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 3: Bad file descriptor\n")]
    [InlineData("value=old; exec 3</dev/null; exec 4>/dev/null; read -u3 -u4 value; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 4: Bad file descriptor\n")]
    [InlineData("value=old; read value 0<&-; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("value=old; read -u0 value 0<&-; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: 0: invalid file descriptor: Bad file descriptor\n")]
    [InlineData("read 1bad 0>/dev/null; printf '<%s>' \"$?\"", "<1>", "read: `1bad': not a valid identifier\n")]
    [InlineData("value=old; read value 1bad 0>/dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("i=0; read 'values[i++]' 0>/dev/null; printf '<%s><%s>' \"$?\" \"$i\"", "<1><0>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("value=old; readonly value; read value 0>/dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("REPLY=old; read 0>/dev/null; printf '<%s><%s>' \"$?\" \"$REPLY\"", "<1><old>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("value=old; ! read value 0>/dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<0><old>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("value=old; value=temp read value 0>/dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("value=old; read value 0>/dev/null 2>&1; printf '<%s><%s>' \"$?\" \"$value\"", "read: read error: 0: Bad file descriptor\n<1><old>", "")]
    [InlineData("value=old; read value 0>/dev/null 2>&-; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "")]
    [InlineData("value=old; read value </dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<1><>", "")]
    [InlineData("value=$(read 0>/dev/null); printf '<%s><%s>' \"$?\" \"$value\"", "<1><>", "read: read error: 0: Bad file descriptor\n")]
    [InlineData("value=old; read -u3 value 3>/dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 3: Bad file descriptor\n")]
    [InlineData("value=old; read -u3 value 3>>/dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 3: Bad file descriptor\n")]
    [InlineData("value=old; read -u3 value 3<>/dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "<1><>", "")]
    [InlineData("exec 3>/dev/null; exec 4<&3; value=old; read -u4 value; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 4: Bad file descriptor\n")]
    [InlineData("value=old; : {fd}>/dev/null; read -u \"$fd\" value; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old>", "read: read error: 10: Bad file descriptor\n")]
    [InlineData("value=old; : {fd}</dev/null; read -u \"$fd\" value; printf '<%s><%s>' \"$?\" \"$value\"", "<1><>", "")]
    [InlineData("exec 3</dev/null; exec 4<&3; exec 3<&-; read -u4 value; printf '<%s>' \"$?\"", "<1>", "")]
    [InlineData("exec 3>/dev/null; exec 4</dev/null; value=old; read -u3 value; printf '<%s><%s>' \"$?\" \"$value\"; read -u4 value; printf '<%s><%s>' \"$?\" \"$value\"", "<1><old><1><>", "read: read error: 3: Bad file descriptor\n")]
    public async Task Evaluation_ReadError_DiagnosticsPreserveNativeOrdering(
        string script, string output, string error)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(output), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(error), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task Evaluation_ReadError_UsesLogicalNumberAndHonorsDiagnosticFailure(
        int logicalNumber, bool appendFailed)
    {
        var table = new DescriptorTable();
        var inputHandle = new FileDescriptor(99);
        var errorHandle = new FileDescriptor(42);
        table.Set(new LogicalDescriptor(logicalNumber), inputHandle);
        table.Set(LogicalDescriptor.StdErr, errorHandle);
        var descriptors = new EvaluationDescriptorSetup(table.Capture(), [inputHandle, errorHandle]);
        var command = logicalNumber == 0 ? "read value" : "read -u3 value";
        var state = new Eval(Parser.Parse(Utf8("value=old; " + command)), descriptors,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, [], new ExpansionBudget());

        var firstRead = await NextReadAsync(state);
        Assert.Equal(inputHandle, firstRead.FileDescriptor);
        firstRead.SetResult(ReadResult.Success(Utf8Bytes("partial\\\n"), [], reachedEoF: false));
        var failedRead = await NextReadAsync(state);
        Assert.Equal(inputHandle, failedRead.FileDescriptor);
        failedRead.SetResult(ReadResult.Failure(new OperationError("Permission denied\n")));

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(errorHandle, diagnostic.FileDescriptor);
        Assert.Equal($"read: read error: {logicalNumber}: Permission denied\n", Text(diagnostic.Content));
        diagnostic.SetResult(!appendFailed
            ? DescriptorWriteResult.Open : DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("old", Text(GetVariable(state, "value")));
        Assert.Equal(inputHandle, table.Resolve(new LogicalDescriptor(logicalNumber)));
        Assert.Empty(state.GetDescriptorsForCleanup());
    }
}
