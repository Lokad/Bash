using System;
using System.Collections.Generic;
using System.Linq;

namespace Lokad.Bash.Evaluation;

[Flags]
internal enum ShellOption
{
    None = 0,
    Errexit = 1,
    InheritErrexit = 2,
    LocalVariableInheritance = 4,
    BraceExpand = 8,
    Pipefail = 16
}

internal static class ShellOptions
{
    internal const ShellOption Fixed = ShellOption.BraceExpand | ShellOption.Pipefail;
    internal static readonly BindingName BashOptionsName = "BASHOPTS";
    internal static readonly BindingName SetOptionsName = "SHELLOPTS";

    internal readonly record struct Entry(ShellValue Name, ShellOption Option);

    private static readonly Entry[] SetOptions =
    [
        new(ShellValue.FromText("braceexpand"), ShellOption.BraceExpand),
        new(ShellValue.FromText("errexit"), ShellOption.Errexit),
        new(ShellValue.FromText("pipefail"), ShellOption.Pipefail)
    ];

    private static readonly Entry[] BashOptions =
    [
        new(ShellValue.FromText("inherit_errexit"), ShellOption.InheritErrexit),
        new(ShellValue.FromText("localvar_inherit"), ShellOption.LocalVariableInheritance)
    ];

    private static readonly ShellValue[] SetValues =
        [Format(SetOptions, Fixed), Format(SetOptions, Fixed | ShellOption.Errexit)];

    private static readonly ShellValue[] BashValues =
    [
        ShellValue.Empty,
        Format(BashOptions, ShellOption.InheritErrexit),
        Format(BashOptions, ShellOption.LocalVariableInheritance),
        Format(BashOptions, ShellOption.InheritErrexit | ShellOption.LocalVariableInheritance)
    ];

    internal static IReadOnlyList<Entry> Names(bool setOptions) => setOptions ? SetOptions : BashOptions;

    internal static ShellOption Find(ReadOnlySpan<byte> name, bool setOptions)
    {
        foreach (var entry in setOptions ? SetOptions : BashOptions)
            if (name.SequenceEqual(entry.Name.Bytes.Span))
                return entry.Option;
        return ShellOption.None;
    }

    internal static ShellValue FormatBinding(ShellOption enabled, bool setOptions) => setOptions
        ? SetValues[(int)(enabled & ShellOption.Errexit)]
        : BashValues[(int)(enabled & (ShellOption.InheritErrexit | ShellOption.LocalVariableInheritance)) >> 1];

    internal static ShellOption ParseImported(ReadOnlySpan<byte> value, bool setOptions)
    {
        var enabled = ShellOption.None;
        foreach (var component in value.Split((byte)':'))
            enabled |= Find(value[component], setOptions);
        return enabled;
    }

    private static ShellValue Format(Entry[] entries, ShellOption enabled) =>
        ShellValue.FromText(string.Join(':', entries.Where(entry => (enabled & entry.Option) != 0)
            .Select(entry => entry.Name.ToUtf8String())));
}
