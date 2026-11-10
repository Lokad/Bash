using System;
using System.Globalization;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

/// <summary>A nonnegative descriptor number in a script or hosted interpreter, independent of host handles.</summary>
public readonly record struct LogicalDescriptor
{
    public LogicalDescriptor(int number)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        Number = number;
    }

    public int Number { get; }

    public static LogicalDescriptor StdIn => new(0);
    public static LogicalDescriptor StdOut => new(1);
    public static LogicalDescriptor StdErr => new(2);

    public static bool TryParse(ReadOnlySpan<char> text, out LogicalDescriptor descriptor)
    {
        descriptor = default;
        if (!Integers.TryParseDescriptor(text, out var number))
            return false;
        descriptor = new LogicalDescriptor(number);
        return true;
    }

    public override string ToString() => Number.ToString(CultureInfo.InvariantCulture);
}
