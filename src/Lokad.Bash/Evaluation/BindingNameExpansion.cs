using System;
using Lokad.Bash.Commands;

namespace Lokad.Bash.Evaluation;

/// <summary>Reserves binding-name copies under the evaluator's expansion memory.</summary>
internal static class BindingNameExpansion
{
    internal static BindingName FromValue(ShellValue value, EvaluationMemory memory)
    {
        memory.Charge(checked(4L * value.Bytes.Length));
        return BindingName.FromValue(value);
    }

    internal static bool StartsWith(this in BindingName name, BindingName prefix, EvaluationMemory memory)
    {
        if (name.CachedText is { } text && prefix.CachedText is { } prefixText)
            return text.StartsWith(prefixText, StringComparison.Ordinal);

        // Nonmatching names still incur encoding work; reserve it before copying.
        if (name.NeedsValueEncoding)
            memory.Charge(checked(4L * name.ByteLength));
        if (prefix.NeedsValueEncoding)
            memory.Charge(checked(4L * prefix.ByteLength));
        return name.ToValue().Bytes.Span.StartsWith(prefix.ToValue().Bytes.Span);
    }
}
