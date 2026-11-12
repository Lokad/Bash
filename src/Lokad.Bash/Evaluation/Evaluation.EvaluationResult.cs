namespace Lokad.Bash.Evaluation;

public partial class Evaluation
{
    /// <summary> Enumerates the flow-control outcomes produced by evaluation. </summary>
    private enum FlowControlKind
    {
        None,
        Break,
        Continue,
        Return,
        Exit
    }

    /// <summary> Represents the exit code and flow-control outcome of evaluating a node. </summary>
    private readonly struct EvaluationResult(int exitCode, FlowControlKind flow, int level, bool testedStatus)
    {
        public int ExitCode { get; } = exitCode;

        /// <summary>
        /// Gets the flow-control outcome emitted by the evaluated node.
        /// </summary>
        /// <remarks>
        /// This indicates whether evaluation should continue normally (<see cref="FlowControlKind.None"/>) or
        /// unwind control flow for break/continue/return/exit. Break and continue propagate through nested
        /// evaluators using <see cref="Level"/> (see loop evaluators such as
        /// <see cref="Evaluation.EvaluateLoopAsync"/>, <see cref="Evaluation.EvaluateForAsync"/>, and
        /// <see cref="Evaluation.EvaluateArithmeticForAsync"/>). Return and exit bypass loop handling and
        /// propagate to the function/evaluation boundary where they are finalized.
        /// </remarks>
        public FlowControlKind Flow { get; } = flow;

        /// <summary>
        /// Gets the remaining loop levels to apply for break/continue flow control.
        /// </summary>
        /// <remarks>
        /// The value is only meaningful when <see cref="Flow"/> is <see cref="FlowControlKind.Break"/> or
        /// <see cref="FlowControlKind.Continue"/>. It encodes how many loop levels should still be unwound
        /// before the flow is consumed. The break/continue builtins set this from their numeric argument
        /// (see <see cref="Evaluation.EvaluateBuiltinLoopControlAsync"/>),
        /// and each loop evaluation method decrements it as it exits a loop boundary
        /// (see <see cref="Evaluation.EvaluateLoopAsync"/>, <see cref="Evaluation.EvaluateForAsync"/>, and
        /// <see cref="Evaluation.EvaluateArithmeticForAsync"/>). When the level reaches 1, the current loop
        /// consumes the flow (break exits the loop; continue skips to the next iteration). A value greater
        /// than 1 indicates the flow must propagate outward to enclosing loops; a value of 0 is used for
        /// non-loop flow kinds (return/exit/none).
        /// </remarks>
        public int Level { get; } = level;

        /// <summary>A compound command can finish with a failure whose status was already tested.</summary>
        public bool TestedStatus { get; } = testedStatus;

        public EvaluationResult WithExitCode(int exitCode) => new(exitCode, Flow, Level, TestedStatus);

        public EvaluationResult WithLevel(int level) => new(ExitCode, Flow, level, TestedStatus);

        public EvaluationResult WithTestedStatus(bool tested) => new(ExitCode, Flow, Level, tested);

        public static EvaluationResult FromExitCode(int exitCode) => new(exitCode, FlowControlKind.None, 0, false);

        /// <summary>
        /// Creates a break flow result with a remaining loop level count.
        /// </summary>
        public static EvaluationResult Break(int level, int exitCode) => new(exitCode, FlowControlKind.Break, level, false);

        /// <summary>
        /// Creates a continue flow result with a remaining loop level count.
        /// </summary>
        public static EvaluationResult Continue(int level, int exitCode) => new(exitCode, FlowControlKind.Continue, level, false);

        public static EvaluationResult Return(int exitCode) => new(exitCode, FlowControlKind.Return, 0, false);

        public static EvaluationResult Exit(int exitCode) => new(exitCode, FlowControlKind.Exit, 0, false);
    }

}
