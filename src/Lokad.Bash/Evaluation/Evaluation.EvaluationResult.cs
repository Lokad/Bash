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

    private enum ExitCause { Completion, ExtraArguments, InvalidLoopCount, CompoundInitializerSyntax }

    /// <summary> Represents the exit code and flow-control outcome of evaluating a node. </summary>
    private readonly struct EvaluationResult(int exitCode, FlowControlKind flow, int level, bool testedStatus)
    {
        public int ExitCode { get; private init; } = exitCode;

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
        public int Level { get; private init; } = level;

        /// <summary>A compound command can finish with a failure whose status was already tested.</summary>
        public bool TestedStatus { get; private init; } = testedStatus;

        public RedirectionScope? DeferredErrexit { get; private init; }

        private ExitCause Cause { get; init; }

        /// <summary>Unwinds through the active command unit to its outer script reader.</summary>
        public bool DiscardsCommandUnit => Cause == ExitCause.ExtraArguments || DiscardsLoadedCommandUnit;

        /// <summary>Lets an active source or eval reader resume after its rejected unit.</summary>
        public bool DiscardsLoadedCommandUnit => Cause == ExitCause.CompoundInitializerSyntax;

        /// <summary>Restores a function's saved options before reaching the EXIT action.</summary>
        public bool RestoresLocalOptions => DiscardsCommandUnit || Cause == ExitCause.InvalidLoopCount;

        public EvaluationResult WithExitCode(int exitCode) =>
            this with { ExitCode = exitCode };

        public EvaluationResult WithLevel(int level) =>
            this with { Level = level };

        public EvaluationResult WithTestedStatus(bool tested) =>
            this with { TestedStatus = tested };

        public EvaluationResult WithDeferredErrexit(RedirectionScope? redirections) =>
            this with { DeferredErrexit = redirections };

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

        public static EvaluationResult DiscardCommandUnit() =>
            Exit(1) with { Cause = ExitCause.ExtraArguments };

        public static EvaluationResult DiscardCompoundInitializerUnit() =>
            Exit(1) with { Cause = ExitCause.CompoundInitializerSyntax };

        public static EvaluationResult InvalidLoopCount(int exitCode) =>
            Exit(exitCode) with { Cause = ExitCause.InvalidLoopCount };
    }

}
