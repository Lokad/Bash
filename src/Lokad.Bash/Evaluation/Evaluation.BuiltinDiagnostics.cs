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
    private enum BuiltinHelpOutput { Standard, Control, Prepared }

    private Task<DescriptorWriteResult> WriteDiagnosticUnlessStderrClosedAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        BuiltinDiagnosticScope? scope) =>
        WriteDiagnosticAsync(name, message, scope, ClosedStderrHandling.Discard);

    private Task<DescriptorWriteResult> WriteInvalidShortOptionUnlessStderrClosedAsync(
        ShellValue command, ReadOnlyMemory<byte> flag, BuiltinKind kind, string name) =>
        WriteInvalidShortOptionUnlessStderrClosedAsync(command, ShellValue.FromText("-"), flag, kind, name);

    private Task<DescriptorWriteResult> WriteInvalidShortOptionUnlessStderrClosedAsync(
        ShellValue command, ShellValue sign, ReadOnlyMemory<byte> flag, BuiltinKind kind, string name) =>
        WriteDiagnosticUnlessStderrClosedAsync([command], [sign, ShellValue.FromBytes(flag),
            ShellValue.FromText(": invalid option\n" + name + ": usage: " + BuiltinDocumentation.For(kind).Synopsis(name))],
            scope: null);

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

    private Task<EvaluationResult> WriteBuiltinHelpUnlessStdoutClosedAsync(
        ShellValue name, BuiltinKind kind, string commandName, bool negated) =>
        WriteBuiltinHelpUnlessStdoutClosedAsync(name, kind, commandName, BuiltinHelpOutput.Standard, negated);

    private Task<EvaluationResult> WriteControlHelpAsync(ShellValue name, BuiltinKind kind, string commandName, bool negated) =>
        WriteBuiltinHelpUnlessStdoutClosedAsync(name, kind, commandName, BuiltinHelpOutput.Control, negated);

    private Task<EvaluationResult> WriteBuiltinHelpAsync(ShellValue name, string synopsis, string description, bool negated) =>
        WriteBuiltinHelpAsync(name, synopsis, description, BuiltinHelpOutput.Standard, negated);

    private Task<EvaluationResult> WriteBuiltinHelpUnlessStdoutClosedAsync(
        ShellValue name, BuiltinKind kind, string commandName, BuiltinHelpOutput mode, bool negated)
    {
        if (GetStdOutDescriptor().IsClosed)
            return FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated);
        var documentation = BuiltinDocumentation.For(kind);
        return WriteBuiltinHelpAsync(name, documentation.Synopsis(commandName), documentation.Description,
            mode, negated);
    }

    private async Task<EvaluationResult> WriteBuiltinHelpAsync(
        ShellValue name, string synopsis, string description, BuiltinHelpOutput mode, bool negated)
    {
        var encoding = Utf8Text.StrictUtf8;
        var synopsisLength = encoding.GetByteCount(synopsis);
        var descriptionLength = encoding.GetByteCount(description);
        var descriptionPrefix = mode == BuiltinHelpOutput.Control ? "\n"u8 : "\n    "u8;
        using var output = new ByteMemoryBuilder(
            checked(name.Bytes.Length + synopsisLength + descriptionLength + descriptionPrefix.Length + 3), Memory.Budget);
        output.Append(name.Bytes);
        output.Append(": "u8);
        encoding.GetBytes(synopsis.AsSpan(), output.AppendSpan(synopsisLength));
        output.Append(descriptionPrefix);
        encoding.GetBytes(description.AsSpan(), output.AppendSpan(descriptionLength));
        output.Append((byte)'\n');
        var result = await ExecuteWriteAsync(GetStdOutDescriptor(), output.ToMemory()).ConfigureAwait(false);
        if (mode == BuiltinHelpOutput.Standard && result.IsError)
        {
            await ReportBuiltinWriteFailureAsync(name, result).ConfigureAwait(false);
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated).ConfigureAwait(false);
        }
        return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), negated).ConfigureAwait(false);
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
