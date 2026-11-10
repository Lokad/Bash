namespace Lokad.Bash.Commands;

/// <summary> The outcome of one host command invocation, not a pre-execution lookup result. </summary>
public abstract record CommandExecutionResult
{
    private CommandExecutionResult()
    {
    }

    /// <summary> The ordinary shell status; command consumers may define a different mapping. </summary>
    public abstract int ShellExitCode { get; }

    /// <summary> Whether this invocation rejected an argument copy, captured when execution completed. </summary>
    /// <remarks> Independent of the tool's exit status and of other invocations sharing its allocation budget. </remarks>
    public bool ArgumentCopyLimitExceeded { get; init; }

    /// <summary> A resolved tool completed with this status, including values such as 126, 127 or 255. </summary>
    public sealed record Completed(int ExitCode) : CommandExecutionResult
    {
        public override int ShellExitCode => ExitCode;
    }

    /// <summary> The requested command or direct script path was not found; it did not execute. </summary>
    public sealed record NotFound : CommandExecutionResult
    {
        public override int ShellExitCode => 127;
    }

    /// <summary>The command could not start; preserves the launch failure's detail and known cause.</summary>
    public sealed record NotExecutable(OperationError Error) : CommandExecutionResult
    {
        public override int ShellExitCode => 126;
    }
}
