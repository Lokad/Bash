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
    Dotglob = 8,
    Failglob = 16,
    Nullglob = 32,
    NoCaseGlob = 64,
    NoCaseMatch = 128,
    BraceExpand = 256,
    Pipefail = 512
}

internal static class ShellOptions
{
    internal const ShellOption Fixed = ShellOption.BraceExpand | ShellOption.Pipefail;
    private const ShellOption BashMask = ShellOption.InheritErrexit | ShellOption.LocalVariableInheritance
        | ShellOption.Dotglob | ShellOption.Failglob | ShellOption.Nullglob | ShellOption.NoCaseGlob | ShellOption.NoCaseMatch;
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
        new(ShellValue.FromText("dotglob"), ShellOption.Dotglob),
        new(ShellValue.FromText("failglob"), ShellOption.Failglob),
        new(ShellValue.FromText("inherit_errexit"), ShellOption.InheritErrexit),
        new(ShellValue.FromText("localvar_inherit"), ShellOption.LocalVariableInheritance),
        new(ShellValue.FromText("nocaseglob"), ShellOption.NoCaseGlob),
        new(ShellValue.FromText("nocasematch"), ShellOption.NoCaseMatch),
        new(ShellValue.FromText("nullglob"), ShellOption.Nullglob)
    ];

    private static readonly ShellValue[] SetValues =
        [Format(SetOptions, Fixed), Format(SetOptions, Fixed | ShellOption.Errexit)];

    private static readonly ShellValue[] BashValues = BuildBashValues();

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
        : BashValues[(int)(enabled & BashMask) >> 1];

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

    private static ShellValue[] BuildBashValues()
    {
        // The mutable shopt flags occupy consecutive bits above errexit.
        var values = new ShellValue[((int)BashMask >> 1) + 1];
        for (var index = 0; index < values.Length; index++)
            values[index] = Format(BashOptions, (ShellOption)(index << 1));
        return values;
    }
}
