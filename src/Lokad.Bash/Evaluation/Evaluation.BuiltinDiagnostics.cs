using System;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private BuiltinDiagnosticScope? _builtinDiagnostics;

    internal bool BuiltinDiagnosticsOpen => _builtinDiagnostics?.Result.CanAcceptMore != false;

    private enum ClosedStderrHandling { AttemptWrite, Discard }

    private Task<DescriptorWriteResult> WriteDiagnosticUnlessStderrClosedAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        BuiltinDiagnosticScope? scope) =>
        WriteDiagnosticAsync(name, message, scope, ClosedStderrHandling.Discard);

    private Task<DescriptorWriteResult> WriteInvalidIdentifierDiagnosticAsync(ShellValue command, ShellValue operand,
        BuiltinDiagnosticScope? scope, ClosedStderrHandling closedStderr) =>
        WriteQuotedOperandDiagnosticAsync(command, operand, "not a valid identifier", scope, closedStderr);

    private Task<DescriptorWriteResult> WriteQuotedOperandDiagnosticAsync(ShellValue command, ShellValue operand,
        string message, BuiltinDiagnosticScope? scope, ClosedStderrHandling closedStderr) =>
        WriteDiagnosticAsync([command], [ShellValue.FromText("`"), operand, ShellValue.FromText("': " + message)],
            scope, closedStderr);

    private async Task<DescriptorWriteResult> WriteBuiltinOutputAsync(ShellValue name, ReadOnlyMemory<byte> content)
    {
        var result = await ExecuteWriteAsync(GetStdOutDescriptor(), content).ConfigureAwait(false);
        await ReportBuiltinWriteFailureAsync(name, result).ConfigureAwait(false);
        return result;
    }

    private async Task<EvaluationResult> WriteBuiltinHelpAsync(ShellValue name, string synopsis, string description, bool negated)
    {
        var encoding = Utf8Text.StrictUtf8;
        var synopsisLength = encoding.GetByteCount(synopsis);
        var descriptionLength = encoding.GetByteCount(description);
        using var output = new ByteMemoryBuilder(checked(name.Bytes.Length + synopsisLength + descriptionLength + 8), Memory.Budget);
        output.Append(name.Bytes);
        output.Append(": "u8);
        encoding.GetBytes(synopsis.AsSpan(), output.AppendSpan(synopsisLength));
        output.Append("\n    "u8);
        encoding.GetBytes(description.AsSpan(), output.AppendSpan(descriptionLength));
        output.Append((byte)'\n');
        var result = await WriteBuiltinOutputAsync(name, output.ToMemory()).ConfigureAwait(false);
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(result.IsError ? 1 : 2), negated)
            .ConfigureAwait(false);
    }

    /// <summary>Reports a known output failure through the builtin's current stderr mapping.</summary>
    private Task ReportBuiltinWriteFailureAsync(ShellValue name, DescriptorWriteResult result) =>
        result.Error is { } cause && !GetStdErrDescriptor().IsClosed
            && GetStdErrDescriptor() != GetStdOutDescriptor()
            ? ReportBuiltinOperationErrorAsync(name, "write error: ", path: null, cause)
            : Task.CompletedTask;

    private async Task ReportBuiltinOperationErrorAsync(ShellValue name, string prefix, string? path, OperationError error)
    {
        var scope = _builtinDiagnostics;
        if (scope is { Result.CanAcceptMore: false })
            return;

        var result = await TryWriteOperationDiagnosticAsync(GetStdErrDescriptor(), name, prefix, path, error).ConfigureAwait(false)
            ?? throw new MemoryBudgetExceededException();
        if (scope != null)
            scope.Result = result;
    }

    /// <summary>
    /// Retains the first diagnostic closure or failure within a builtin invocation.
    /// The evaluator owns this state serially; child evaluations have their own diagnostic scope.
    /// </summary>
    private sealed class BuiltinDiagnosticScope : IDisposable
    {
        private readonly Evaluation _owner;
        private readonly BuiltinDiagnosticScope? _previous;

        public BuiltinDiagnosticScope(Evaluation owner)
        {
            _owner = owner;
            _previous = owner._builtinDiagnostics;
            owner._builtinDiagnostics = this;
        }

        public DescriptorWriteResult Result { get; set; } = DescriptorWriteResult.Open;

        public void Dispose() => _owner._builtinDiagnostics = _previous;
    }
}
