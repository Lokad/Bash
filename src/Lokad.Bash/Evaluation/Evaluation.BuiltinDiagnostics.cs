using System;
using System.Threading.Tasks;
using Lokad.Bash.Files;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private BuiltinDiagnosticScope? _builtinDiagnostics;

    internal bool BuiltinDiagnosticsOpen => _builtinDiagnostics?.Result.CanAcceptMore != false;

    /// <summary>Reports a known output failure through the builtin's current stderr mapping.</summary>
    private Task ReportBuiltinWriteFailureAsync(ShellValue name, DescriptorWriteResult result) =>
        result.Error is { } cause && !GetStdErrDescriptor().IsClosed
            && GetStdErrDescriptor() != GetStdOutDescriptor()
            ? ReportExpansionDiagnosticAsync(name, ShellValue.FromText("write error: " + cause.MessageWithoutTerminator))
            : Task.CompletedTask;

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
