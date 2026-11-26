namespace Lokad.Bash.Evaluation;

/// <summary>Controls how the <c>read</c> builtin interprets input.</summary>
internal readonly record struct ReadBuiltinOptions(
    FieldSeparators FieldSeparators,
    ReadBackslashMode BackslashMode,
    ReadFieldMode FieldMode);

/// <summary>Controls how the <c>read</c> builtin handles backslashes.</summary>
internal enum ReadBackslashMode
{
    InterpretEscapes,
    Preserve,
}

/// <summary>Selects positional or array field splitting.</summary>
internal enum ReadFieldMode
{
    Split,
    SplitAll,
}
