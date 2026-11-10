using System;
using System.Threading;
using Lokad.Bash.Commands;

namespace Lokad.Bash;

/// <summary>Tracks evaluation and resolved commands within one shell invocation.</summary>
/// <remarks>Hosts bind this shared monitor to their descriptors through <see cref="ShellExecutionContext"/>.</remarks>
public interface IShellExecutionMonitor
{
    /// <summary>The command executing in the current asynchronous context, if any.</summary>
    IShellCommandMonitor? CurrentCommand { get; }

    /// <summary>
    /// Enters an evaluator, suspending its caller until evaluation and cleanup finish.
    /// Each pipeline stage and nested shell has its own evaluation scope.
    /// </summary>
    IDisposable BeginEvaluation();

    /// <summary>Tracks the writers and pending reads of one streaming pipe, when supported.</summary>
    IShellPipeMonitor? CreatePipe();

    /// <summary>
    /// Enters one command's execution context, suspending its caller. The scope includes descriptor cleanup and
    /// is disposed when the command finishes, including on failure or cancellation.
    /// The token includes any additional timeout imposed by a nested invocation.
    /// </summary>
    IDisposable BeginCommand(CommandInvocation command, CancellationToken cancellationToken);
}

/// <summary>Connects passive input waits to their upstream execution scopes.</summary>
public interface IShellPipeMonitor
{
    /// <summary>
    /// Registers the current execution as a writer before it starts work, through descriptor cleanup.
    /// Nested executions can produce on behalf of this writer. Descriptor aliases share this monitor.
    /// </summary>
    IDisposable BeginWrite();

    /// <summary>
    /// Marks the current execution as waiting for pipe input or access to its read endpoint,
    /// until disposal. Never include processing buffered data, and end an access wait before
    /// starting the inner read. Returns null outside a tracked execution.
    /// Overlapping reader-close waits on the same pipe in one execution count as one dependency
    /// until their last scope is disposed. Independent read waits remain separate.
    /// </summary>
    /// <param name="kind">Whether this is an independent read or the reader's shared terminal close.</param>
    IDisposable? BeginRead(ShellPipeWaitKind kind);
}

/// <summary>Distinguishes independent input waits from callers awaiting the reader's single close.</summary>
public enum ShellPipeWaitKind
{
    /// <summary>Waits for input or access to the read endpoint for an independent operation.</summary>
    Read,
    /// <summary>Waits for the read endpoint's shared terminal cleanup.</summary>
    CloseReader
}

/// <summary>Allows a command to opt into heartbeat supervision beyond the ordinary shell budget.</summary>
public interface IShellCommandMonitor
{
    /// <summary>
    /// Opts into a bounded total duration and an inactivity limit, starting now.
    /// Call once at execution start, then emit heartbeats from work checkpoints.
    /// This command and passive pipe readers waiting on it stop consuming the shared
    /// ordinary budget; other parallel work retains it. A caller's explicit invocation
    /// timeout overrides the total duration.
    /// Supervision lasts through command completion, including descriptor cleanup.
    /// </summary>
    IShellHeartbeatMonitor EnableHeartbeats(TimeSpan maximumDuration, TimeSpan maximumSilence);
}

/// <summary>Reports activity for one opted-in command; completion makes retained handles inactive.</summary>
public interface IShellHeartbeatMonitor
{
    /// <summary>
    /// Records a work checkpoint and checks cancellation. Never renews the total time cap
    /// or revives expired work. Calls after command completion are ignored.
    /// </summary>
    void Heartbeat();

    /// <summary>
    /// Enters one bounded external wait that cannot report work checkpoints. No synthetic
    /// heartbeats are emitted: the wait has its own deadline within the total command cap.
    /// Disposing the scope resumes inactivity monitoring from the actual completion time.
    /// Only one wait may be active, and heartbeats cannot extend it.
    /// </summary>
    IDisposable BeginWait(string operation, TimeSpan timeout);
}
