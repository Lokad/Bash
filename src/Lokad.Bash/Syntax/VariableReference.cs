using System;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>A variable name with an optional, unexpanded array selector.</summary>
internal readonly record struct VariableReference
{
    private VariableReference(BindingName name, VariableReferenceKind kind, ShellSource subscript)
    {
        Name = name;
        Kind = kind;
        Subscript = subscript;
    }

    public BindingName Name { get; }
    public VariableReferenceKind Kind { get; }

    /// <summary>Retains quotes and escapes until the destination array kind is known.</summary>
    public ShellSource Subscript { get; }

    public bool IsAggregate => Kind is VariableReferenceKind.At or VariableReferenceKind.Star;

    /// <summary>Creates a reference to an already selected binding, including literal local names.</summary>
    public static VariableReference ForName(BindingName name) => new(name, VariableReferenceKind.Name, ShellSource.Empty);

    internal ShellValue ToValue(EvaluationMemory? memory)
    {
        var nameLength = Name.ByteLength;
        var length = nameLength + (Kind == VariableReferenceKind.Name ? 0L : Subscript.Length + 2L);
        if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();
        memory?.Charge(checked(4L * (Kind == VariableReferenceKind.Name ? nameLength : nameLength + length)));
        var name = Name.ToValue();
        return Kind == VariableReferenceKind.Name ? name
            : ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted((int)length)
                .Append(name.Bytes).Append((byte)'[').Append(Subscript.Bytes).Append((byte)']').ToMemory());
    }

    /// <summary>Recognizes a whole reference without expanding or flattening its selector.</summary>
    public static bool TryParse(string text, out VariableReference reference)
        => TryParse(text, memory: null, out reference);

    internal static bool TryParse(string text, EvaluationMemory? memory, out VariableReference reference)
    {
        if (!text.Contains('['))
        {
            reference = default;
            if (!VariableNames.IsValidVariableName(text.AsSpan()))
                return false;
            reference = new VariableReference(text, VariableReferenceKind.Name, ShellSource.Empty);
            return true;
        }
        memory?.Charge(checked(4L * (System.Text.Encoding.UTF8.GetByteCount(text))));
        return TryParse(ShellSource.FromText(text), memory, out reference);
    }

    public static bool TryParse(ShellSource text, out VariableReference reference)
        => TryParse(text, memory: null, out reference);

    internal static bool TryParse(ShellSource text, EvaluationMemory? memory, out VariableReference reference)
    {
        reference = default;
        var opening = text.AsSpan().IndexOf((byte)'[');
        memory?.Charge(checked(4L * (opening < 0 ? text.Length : opening)));
        if (opening < 0)
        {
            if (!VariableNames.IsValidVariableName(text.AsSpan()))
                return false;

            reference = new VariableReference(text.ToUtf8String(), VariableReferenceKind.Name, ShellSource.Empty);
            return true;
        }

        if (!VariableNames.IsValidVariableName(text.AsSpan(0, opening))
            || !ArraySubscriptScanner.TryScan(text, opening, out var closing)
            || closing != text.Length - 1)
            return false;

        var subscript = text.Slice(opening + 1, closing - opening - 1);
        reference = ForElement(text.Slice(0, opening).ToUtf8String(), subscript);
        return true;
    }

    /// <summary>Creates a selector from an already separated name and subscript.</summary>
    public static VariableReference ForElement(BindingName name, ShellSource subscript) =>
        new(name, subscript.AsSpan() switch
        {
            [(byte)'@'] => VariableReferenceKind.At,
            [(byte)'*'] => VariableReferenceKind.Star,
            _ => VariableReferenceKind.Element,
        }, subscript);
}

internal enum VariableReferenceKind
{
    Name,
    Element,
    At,
    Star,
}
