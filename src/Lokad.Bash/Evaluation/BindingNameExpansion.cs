using System;
using Lokad.Bash.Commands;

namespace Lokad.Bash.Evaluation;

/// <summary>Reserves binding-name copies under the evaluator's expansion allowance.</summary>
internal static class BindingNameExpansion
{
    internal static BindingName FromValue(ShellValue value, AllocationAllowance allowance)
    {
        allowance.Charge(0, value.Bytes.Length);
        return BindingName.FromValue(value);
    }

    internal static bool StartsWith(this in BindingName name, BindingName prefix, AllocationAllowance allowance)
    {
        if (name.CachedText is { } text && prefix.CachedText is { } prefixText)
            return text.StartsWith(prefixText, StringComparison.Ordinal);

        // Nonmatching names still incur encoding work; reserve it before copying.
        if (name.NeedsValueEncoding)
            allowance.Charge(0, name.ByteLength);
        if (prefix.NeedsValueEncoding)
            allowance.Charge(0, prefix.ByteLength);
        return name.ToValue().Bytes.Span.StartsWith(prefix.ToValue().Bytes.Span);
    }
}
