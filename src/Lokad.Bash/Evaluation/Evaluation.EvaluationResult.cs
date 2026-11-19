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
        /// consumes the flow according to its condition/body phase. A value greater
        /// than 1 indicates the flow must propagate outward to enclosing loops; a value of 0 is used for
        /// non-loop flow kinds (return/exit/none).
        /// </remarks>
        public int Level { get; } = level;

        /// <summary>A compound command can finish with a failure whose status was already tested.</summary>
        public bool TestedStatus { get; } = testedStatus;

        public RedirectionScope? DeferredErrexit { get; private init; }

        public EvaluationResult WithExitCode(int exitCode) =>
            new(exitCode, Flow, Level, TestedStatus) { DeferredErrexit = DeferredErrexit };

        public EvaluationResult WithLevel(int level) =>
            new(ExitCode, Flow, level, TestedStatus) { DeferredErrexit = DeferredErrexit };

        public EvaluationResult WithTestedStatus(bool tested) =>
            new(ExitCode, Flow, Level, tested) { DeferredErrexit = DeferredErrexit };

        public EvaluationResult WithDeferredErrexit(RedirectionScope? redirections) =>
            new(ExitCode, Flow, Level, TestedStatus) { DeferredErrexit = redirections };

        /// <summary>Propagates return/exit or the remaining levels of loop flow past one loop owner.</summary>
        public bool TryPropagateLoopFlow(out EvaluationResult outward)
        {
            outward = this;
            if (Flow is FlowControlKind.Break or FlowControlKind.Continue)
            {
                if (Level <= 1)
                    return false;
                outward = WithLevel(Level - 1);
                return true;
            }
            return Flow != FlowControlKind.None;
        }

        /// <summary>Consumes local loop flow without losing the origin of its final status.</summary>
        public EvaluationResult ConsumeLoopFlow() => new(ExitCode, FlowControlKind.None, 0, TestedStatus);

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
