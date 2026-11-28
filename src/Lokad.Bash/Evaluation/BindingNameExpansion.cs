using System;
using System.Text;

namespace Lokad.Bash.Evaluation;

/// <summary>Reserves binding-name copies under the evaluator's expansion memory.</summary>
internal static class BindingNameExpansion
{
    internal static BindingName FromValue(ShellValue value, EvaluationMemory memory)
    {
        memory.Charge(checked(4L * value.Bytes.Length));
        return BindingName.FromValue(value);
    }

    internal static bool StartsWith(this in BindingName name, ReadOnlySpan<byte> prefix, BindingName? decodedPrefix,
        EvaluationMemory memory)
    {
        if (name.CachedText is { } text)
        {
            // An absent decoded value marks a prefix whose syntax bytes are all ASCII.
            if (decodedPrefix is not { } binding)
                return text.Length >= prefix.Length && Ascii.Equals(prefix, text.AsSpan(0, prefix.Length));
            if (binding.CachedText is { } prefixText)
                return text.StartsWith(prefixText, StringComparison.Ordinal);
            if (name.NeedsValueEncoding)
            {
                // An undecodable prefix cannot match an ASCII spelling.
                if (Ascii.IsValid(text))
                    return false;
                memory.Charge(checked(4L * name.ByteLength));
            }
        }

        return name.ToValue().Bytes.Span.StartsWith(prefix);
    }
}
