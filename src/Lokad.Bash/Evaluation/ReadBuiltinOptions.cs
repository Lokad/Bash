namespace Lokad.Bash.Evaluation;

/// <summary>Controls how the <c>read</c> builtin interprets input.</summary>
internal sealed record ReadBuiltinOptions(
    FieldSeparators FieldSeparators,
    ReadBackslashMode BackslashMode,
    ReadFieldMode FieldMode);

/// <summary>Controls how the <c>read</c> builtin handles backslashes.</summary>
internal enum ReadBackslashMode
{
    InterpretEscapes,
    Preserve,
}

/// <summary>Controls whether the <c>read</c> builtin splits input into fields.</summary>
internal enum ReadFieldMode
{
    PreserveInput,
    Split,
}
