using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private readonly record struct GlobIgnoreDiagnostic(FileDescriptor Descriptor, ShellValue? ReaderName,
        int Line, UnexpectedSyntax? Token, UnclosedDelimiter? Delimiter);

    private List<GlobIgnoreDiagnostic>? _globIgnoreDiagnostics;
    private MemoryCapacity? _globIgnoreDiagnosticMemory;

    private bool HasFatalGlobIgnoreSyntax => _state.FatalExpansion is { Kind: ExpansionFailureKind.GlobIgnoreSyntax };

    internal void RecordGlobIgnoreDiagnostic(ShellSource source, ParserFailure failure, ScopedEvaluation scope)
    {
        if (HasFatalGlobIgnoreSyntax || failure is { UnexpectedToken: null, UnclosedDelimiter: null })
            return;
        if (_state.ErrexitEnabled)
            SetFatalExpansionError(new ExpansionFailure(ExpansionFailureKind.GlobIgnoreSyntax, 2));

        var descriptor = GetStdErrDescriptor();
        if (descriptor.IsClosed)
            return;

        var owner = _globIgnoreDiagnosticMemory ??= new MemoryCapacity(Memory.Budget, 0);
        if (_globIgnoreDiagnostics == null)
        {
            Memory.Charge(owner, 32);
            _globIgnoreDiagnostics = [];
        }
        var token = failure.UnexpectedToken;
        var delimiter = failure.UnclosedDelimiter;
        var line = Math.Max(1, scope.Context.Coordinates.Line);
        if (delimiter is { } unclosed)
            line += source.AsSpan(0, unclosed.ReadPosition).Count((byte)'\n') + unclosed.LineAdvance + 1;
        else if (token is { } unexpected)
        {
            line = GetCaptureDiagnosticLine(unexpected, source.Bytes, line);
            token = TrimCaptureDiagnosticLine(unexpected, source.Bytes);
        }
        var reader = scope.Context.DiagnosticName;
        // The selected spelling and parser's workspace may retire before the descriptor write settles.
        Memory.Charge(owner, 512L + 4L * (source.Length + (long)(reader?.Bytes.Length ?? 0)
            + (token?.Line.Length ?? 0) + (token?.Token.Length ?? 0) + (token?.Argument.Length ?? 0)));
        Memory.PrepareListCapacity(owner, _globIgnoreDiagnostics, 1);
        _globIgnoreDiagnostics.Add(new GlobIgnoreDiagnostic(descriptor, reader, line, token, delimiter));
    }

    private Task FlushGlobIgnoreDiagnosticsAsync()
    {
        if (_globIgnoreDiagnostics is not { } diagnostics)
            return Task.CompletedTask;
        var owner = _globIgnoreDiagnosticMemory;
        _globIgnoreDiagnostics = null;
        _globIgnoreDiagnosticMemory = null;
        return FlushAsync(diagnostics, owner);

        async Task FlushAsync(List<GlobIgnoreDiagnostic> pending, MemoryCapacity? memory)
        {
            using var admitted = memory;
            foreach (var item in pending)
            {
                if (item.Delimiter is { } unclosed)
                    await ReportUnclosedDelimiterAsync(item.Descriptor, unclosed, item.Line, item.ReaderName,
                        ShellValue.Empty, SyntaxDiagnosticOrigin.CommandSubstitution, SyntaxDiagnosticDetail.PrimaryOnly)
                        .ConfigureAwait(false);
                else if (item.Token is { } token)
                    await ReportUnexpectedSyntaxAsync(item.Descriptor, token, item.ReaderName, ShellValue.Empty,
                        SyntaxDiagnosticOrigin.CommandSubstitution, item.Line, SyntaxDiagnosticDetail.Complete)
                        .ConfigureAwait(false);
            }
        }
    }

    private void ReleaseGlobIgnoreDiagnostics()
    {
        _globIgnoreDiagnostics = null;
        _globIgnoreDiagnosticMemory?.Dispose();
        _globIgnoreDiagnosticMemory = null;
    }
}
