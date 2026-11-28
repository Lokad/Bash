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

    private static string InvalidDecimalOperandSuffix(ShellValue operand) =>
        operand.Bytes.Span.StartsWith("0x"u8) ? ": invalid hex number" : ": invalid number";

    private Task<DescriptorWriteResult> WriteDiagnosticUnlessStderrClosedAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        BuiltinDiagnosticScope? scope) =>
        WriteDiagnosticUnlessStderrClosedAsync(name, message, scope, default);

    private Task<DescriptorWriteResult> WriteDiagnosticUnlessStderrClosedAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        BuiltinDiagnosticScope? scope, SyntaxDiagnosticPrefix prefix) =>
        WriteDiagnosticAsync(name, message, scope, ClosedStderrHandling.Discard, prefix);

    private Task ReportBuiltinDiagnosticAsync(ShellValue name, string message, EvaluationContext context) =>
        ReportBuiltinDiagnosticAsync([name], [ShellValue.FromText(message)], context);

    internal Task ReportBuiltinDiagnosticAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        EvaluationContext context) => WriteDiagnosticAsync(name, message, _builtinDiagnostics,
            ClosedStderrHandling.AttemptWrite, GetBuiltinDiagnosticPrefix(context));

    internal Task ReportReadonlyUnsetAsync(ShellValue name, bool function, EvaluationContext context) =>
        ReportBuiltinDiagnosticAsync([ShellValue.FromText("unset")],
            [name, ShellValue.FromText(function ? ": cannot unset: readonly function" : ": cannot unset: readonly variable")], context);

    private Task<DescriptorWriteResult> WriteInvalidShortOptionUnlessStderrClosedAsync(
        ShellValue command, ShellValue sign, ReadOnlyMemory<byte> flag, BuiltinKind kind, string name) =>
        WriteInvalidShortOptionUnlessStderrClosedAsync(command, sign, flag, kind, name, default);

    private Task<DescriptorWriteResult> WriteInvalidShortOptionUnlessStderrClosedAsync(
        ShellValue command, ShellValue sign, ReadOnlyMemory<byte> flag, BuiltinKind kind, string name,
        SyntaxDiagnosticPrefix sourcePrefix) =>
        WriteBuiltinOptionDiagnosticAsync(command, sign, ShellValue.FromBytes(flag), "invalid option", kind, name, sourcePrefix);

    private Task<DescriptorWriteResult> WriteBuiltinOptionDiagnosticAsync(
        ShellValue command, ShellValue optionPrefix, ShellValue option, string detail,
        BuiltinKind kind, string name, SyntaxDiagnosticPrefix sourcePrefix)
    {
        var descriptor = GetStdErrDescriptor();
        if (descriptor.IsClosed)
        {
            CheckCancellation();
            return Task.FromResult(DescriptorWriteResult.Closed);
        }

        var synopsis = kind == BuiltinKind.Type ? "type [-afptP] name [name ...]" : BuiltinDocumentation.For(kind).Synopsis(name);
        return WriteDiagnosticAsync(descriptor, [command], [optionPrefix, option,
            ShellValue.FromText(": " + detail + "\n" + name + ": usage: " + synopsis)], scope: null, sourcePrefix);
    }

    private Task<DescriptorWriteResult> WriteInvalidIdentifierDiagnosticAsync(ShellValue command, ShellValue operand,
        BuiltinDiagnosticScope? scope, ClosedStderrHandling closedStderr) =>
        WriteInvalidIdentifierDiagnosticAsync(command, operand, scope, closedStderr, default);

    private Task<DescriptorWriteResult> WriteInvalidIdentifierDiagnosticAsync(ShellValue command, ShellValue operand,
        BuiltinDiagnosticScope? scope, ClosedStderrHandling closedStderr, SyntaxDiagnosticPrefix prefix) =>
        WriteQuotedOperandDiagnosticAsync(command, operand, "not a valid identifier", scope, closedStderr, prefix);

    internal Task ReportInvalidAssignmentIdentifierAsync(ShellValue command, ShellValue operand, ScopedEvaluation scope) =>
        WriteInvalidIdentifierDiagnosticAsync(command, operand, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite,
            GetCommandDiagnosticPrefix(scope));

    private Task<DescriptorWriteResult> WriteQuotedOperandDiagnosticAsync(ShellValue command, ShellValue operand,
        string message, BuiltinDiagnosticScope? scope, ClosedStderrHandling closedStderr, SyntaxDiagnosticPrefix prefix) =>
        command.IsEmpty
            ? WriteDiagnosticAsync([ShellValue.FromText("`"), operand, ShellValue.FromText("'")], [ShellValue.FromText(message)],
                scope, closedStderr, prefix)
            : WriteDiagnosticAsync([command], [ShellValue.FromText("`"), operand, ShellValue.FromText("': " + message)],
                scope, closedStderr, prefix);

    private Task<DescriptorWriteResult> WriteBuiltinOutputAsync(ShellValue name, ReadOnlyMemory<byte> content) =>
        WriteBuiltinOutputAsync(name, content, default);

    private async Task<DescriptorWriteResult> WriteBuiltinOutputAsync(ShellValue name, ReadOnlyMemory<byte> content,
        SyntaxDiagnosticPrefix sourcePrefix)
    {
        var result = await ExecuteWriteAsync(GetStdOutDescriptor(), content).ConfigureAwait(false);
        await ReportBuiltinWriteFailureAsync(name, result, sourcePrefix).ConfigureAwait(false);
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
        ReportBuiltinWriteFailureAsync(name, result, default);

    private Task ReportBuiltinWriteFailureAsync(ShellValue name, DescriptorWriteResult result,
        SyntaxDiagnosticPrefix sourcePrefix) =>
        result.Error is { } cause && !GetStdErrDescriptor().IsClosed
            && GetStdErrDescriptor() != GetStdOutDescriptor()
            ? ReportBuiltinOperationErrorAsync(name, "write error: ", path: null, cause, sourcePrefix)
            : Task.CompletedTask;

    private async Task ReportBuiltinOperationErrorAsync(ShellValue name, string prefix, string? path, OperationError error,
        SyntaxDiagnosticPrefix sourcePrefix)
    {
        var scope = _builtinDiagnostics;
        if (scope is { Result.CanAcceptMore: false })
            return;

        var result = await TryWriteOperationDiagnosticAsync(GetStdErrDescriptor(), name, prefix, path, error, sourcePrefix,
                relativeDirectory: null, detailPrefix: string.Empty)
                .ConfigureAwait(false)
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
