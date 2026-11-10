using System;
using System.Threading;
using Lokad.Bash.Commands;

namespace Lokad.Bash;

/// <summary>Creates monitored execution scopes with the pipe writers resolved by one host.</summary>
/// <remarks>
/// The monitor may be shared with other hosts; this context and its resolver belong to one host.
/// Scope creation is synchronous to preserve the monitor's ambient execution context. Failed
/// registration releases acquired scopes; successful scopes release writers before execution.
/// Neither this context nor its scopes dispose the shared monitor.
/// </remarks>
/// <param name="monitor">The shared execution monitor.</param>
/// <param name="resolvePipeWriter">
/// Resolves a host handle to its pipe monitor, or null for other descriptors. Aliases return the
/// same monitor. Resolution borrows the monitor without opening, consuming or closing the descriptor.
/// </param>
public sealed class ShellExecutionContext(
    IShellExecutionMonitor monitor,
    Func<FileDescriptor, IShellPipeMonitor?> resolvePipeWriter)
{
    /// <summary>The command executing in the current asynchronous context, if any.</summary>
    public IShellCommandMonitor? CurrentCommand => monitor.CurrentCommand;

    /// <summary>Enters evaluation and registers its output writers before expansion or builtins start.</summary>
    /// <remarks>Keep the returned scope through evaluation and descriptor cleanup.</remarks>
    public IDisposable BeginEvaluation(FileDescriptor stdOut, FileDescriptor stdErr) =>
        RegisterWriters(monitor.BeginEvaluation(), stdOut, stdErr);

    /// <summary>Enters a command and registers its output writers before any command work starts.</summary>
    /// <remarks>Keep the returned scope through command execution and descriptor cleanup.</remarks>
    public IDisposable BeginCommand(CommandInvocation command, CancellationToken cancellationToken) =>
        RegisterWriters(monitor.BeginCommand(command, cancellationToken), command.StdOut, command.StdErr);

    private IDisposable RegisterWriters(IDisposable execution, FileDescriptor stdOut, FileDescriptor stdErr)
    {
        IDisposable? output = null;
        try
        {
            var outputPipe = resolvePipeWriter(stdOut);
            var errorPipe = stdOut == stdErr ? outputPipe : resolvePipeWriter(stdErr);
            if (outputPipe is null && errorPipe is null)
                return execution;

            output = outputPipe?.BeginWrite();
            var error = errorPipe is null || ReferenceEquals(outputPipe, errorPipe) ? null : errorPipe.BeginWrite();
            return new ExecutionScope(execution, output, error);
        }
        catch
        {
            // Registration did not transfer a scope to the caller. Unwind acquired ownership
            // without replacing the registration failure with a cleanup exception.
            try { output?.Dispose(); }
            catch { }
            try { execution.Dispose(); }
            catch { }
            throw;
        }
    }

    private sealed class ExecutionScope(IDisposable execution, IDisposable? output, IDisposable? error) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                try { error?.Dispose(); }
                finally { output?.Dispose(); }
            }
            finally { execution.Dispose(); }
        }
    }
}
