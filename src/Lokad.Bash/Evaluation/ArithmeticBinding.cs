namespace Lokad.Bash.Evaluation;

/// <summary>Whether a reached reference needs its current value or only an assignment destination.</summary>
internal enum ArithmeticReferenceUse
{
    Read,
    Assign,
}

/// <summary>
/// A selected arithmetic location and value, or a binding failure. Selection may perform shell effects.
/// Read bindings must not reject a later readonly write before its right-hand side has executed.
/// </summary>
internal abstract record ArithmeticBinding<TTarget>
{
    private ArithmeticBinding() { }

    internal sealed record Bound(TTarget Target, long Value) : ArithmeticBinding<TTarget>;
    internal sealed record Failure(ArithmeticError Error) : ArithmeticBinding<TTarget>;
}

/// <summary>A completed arithmetic value or a failure with no usable numeric result.</summary>
internal readonly record struct ArithmeticResult(long Value, ArithmeticError? Error);
