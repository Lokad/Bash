using System;
using System.Collections.Generic;
using System.Text;
using Lokad.Bash.Memory;

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
    GlobSkipDots = 256,
    BraceExpand = 512,
    Pipefail = 1024,
    Noglob = 2048,
    XpgEcho = 4096,
    SourcePath = 8192
}

internal static class ShellOptions
{
    internal const ShellOption Fixed = ShellOption.Pipefail;
    private const ShellOption DefaultBashFlags = ShellOption.GlobSkipDots | ShellOption.SourcePath;
    internal const ShellOption Default = Fixed | ShellOption.BraceExpand | DefaultBashFlags;
    internal const ShellOption MutableSet = ShellOption.Errexit | ShellOption.Noglob | ShellOption.BraceExpand;
    internal const ShellOption LocalSnapshot = MutableSet | ShellOption.InheritErrexit | ShellOption.SourcePath;
    internal const byte ErrexitFlag = (byte)'e';
    internal const byte NoglobFlag = (byte)'f';
    internal const byte BraceExpandFlag = (byte)'B';
    internal static readonly BindingName BashOptionsName = "BASHOPTS";
    internal static readonly BindingName SetOptionsName = "SHELLOPTS";

    internal readonly record struct Entry(ShellValue Name, ShellOption Option);

    private static readonly Entry[] SetOptions =
    [
        new(ShellValue.FromText("braceexpand"), ShellOption.BraceExpand),
        new(ShellValue.FromText("errexit"), ShellOption.Errexit),
        new(ShellValue.FromText("noglob"), ShellOption.Noglob),
        new(ShellValue.FromText("pipefail"), ShellOption.Pipefail)
    ];

    private static readonly Entry[] BashOptions =
    [
        new(ShellValue.FromText("dotglob"), ShellOption.Dotglob),
        new(ShellValue.FromText("failglob"), ShellOption.Failglob),
        new(ShellValue.FromText("globskipdots"), ShellOption.GlobSkipDots),
        new(ShellValue.FromText("inherit_errexit"), ShellOption.InheritErrexit),
        new(ShellValue.FromText("localvar_inherit"), ShellOption.LocalVariableInheritance),
        new(ShellValue.FromText("nocaseglob"), ShellOption.NoCaseGlob),
        new(ShellValue.FromText("nocasematch"), ShellOption.NoCaseMatch),
        new(ShellValue.FromText("nullglob"), ShellOption.Nullglob),
        new(ShellValue.FromText("sourcepath"), ShellOption.SourcePath),
        new(ShellValue.FromText("xpg_echo"), ShellOption.XpgEcho)
    ];

    private static readonly ShellOption BashMask = CollectBashFlags();
    private static readonly ShellOption[] SetStates =
    [
        ShellOption.None, ShellOption.Errexit, ShellOption.Noglob, ShellOption.Errexit | ShellOption.Noglob,
        ShellOption.BraceExpand, ShellOption.BraceExpand | ShellOption.Errexit,
        ShellOption.BraceExpand | ShellOption.Noglob, MutableSet
    ];

    internal static ShellValue DefaultBashBinding { get; } = Format(BashOptions, DefaultBashFlags, memory: null, out _);

    internal static IReadOnlyList<Entry> Names(bool setOptions) => setOptions ? SetOptions : BashOptions;

    internal static ShellOption Find(ReadOnlySpan<byte> name, bool setOptions)
    {
        foreach (var entry in setOptions ? SetOptions : BashOptions)
            if (name.SequenceEqual(entry.Name.Bytes.Span))
                return entry.Option;
        return ShellOption.None;
    }

    internal static ShellOption FindShortSetOption(byte flag) => flag switch
    {
        ErrexitFlag => ShellOption.Errexit,
        NoglobFlag => ShellOption.Noglob,
        BraceExpandFlag => ShellOption.BraceExpand,
        _ => ShellOption.None
    };

    private static int MutableSetIndex(ShellOption enabled) =>
        ((enabled & ShellOption.Errexit) != 0 ? 1 : 0) | ((enabled & ShellOption.Noglob) != 0 ? 2 : 0)
        | ((enabled & ShellOption.BraceExpand) != 0 ? 4 : 0);

    internal static ShellValue SetBinding(ShellOption enabled) => SetProjections.Bindings[MutableSetIndex(enabled)];

    internal static ShellValue Flags(ShellOption enabled) => SetProjections.Flags[MutableSetIndex(enabled)];

    internal static ShellValue InspectSetOptions(ShellOption enabled, bool listing)
    {
        var inspection = SetInspections.Values[MutableSetIndex(enabled)];
        return listing ? inspection.Listing : inspection.Commands;
    }

    internal static ShellOption SelectBashFlags(ShellOption enabled) => enabled & BashMask;

    internal static ShellValue FormatBashBinding(ShellOption enabled, EvaluationMemory? memory, out long ownedBytes)
    {
        if (enabled != DefaultBashFlags)
            return Format(BashOptions, enabled, memory, out ownedBytes);

        ownedBytes = 0;
        return DefaultBashBinding;
    }

    internal static ShellOption ParseImported(ReadOnlySpan<byte> value, bool setOptions)
    {
        var enabled = ShellOption.None;
        foreach (var component in value.Split((byte)':'))
            enabled |= Find(value[component], setOptions);
        return enabled;
    }

    private static class SetProjections
    {
        internal static readonly ShellValue[] Bindings = Array.ConvertAll(SetStates,
            static enabled => Format(SetOptions, Fixed | enabled, memory: null, out _));

        // Keep the fixed spellings here so the general byte cache starts on its first ordinary use.
        internal static readonly ShellValue[] Flags =
        [
            ShellValue.Empty, ShellValue.FromText("e"), ShellValue.FromText("f"), ShellValue.FromText("ef"),
            ShellValue.FromText("B"), ShellValue.FromText("eB"), ShellValue.FromText("fB"), ShellValue.FromText("efB")
        ];
    }

    private static class SetInspections
    {
        internal static readonly (ShellValue Listing, ShellValue Commands)[] Values =
            Array.ConvertAll(SetStates, Create);

        private static (ShellValue Listing, ShellValue Commands) Create(ShellOption enabled)
        {
            var listing = new StringBuilder();
            var commands = new StringBuilder();
            foreach (var entry in SetOptions)
            {
                if ((entry.Option & MutableSet) == 0)
                    continue;
                var active = (enabled & entry.Option) != 0;
                var name = entry.Name.ToUtf8String();
                listing.Append(name).Append(active ? "\ton\n" : "\toff\n");
                commands.Append(active ? "set -o " : "set +o ").Append(name).Append('\n');
            }
            return (ShellValue.FromText(listing.ToString()), ShellValue.FromText(commands.ToString()));
        }
    }

    private static ShellValue Format(Entry[] entries, ShellOption enabled, EvaluationMemory? memory, out long ownedBytes)
    {
        ownedBytes = 0;
        var first = ShellValue.Empty;
        var length = 0;
        foreach (var entry in entries)
        {
            if ((enabled & entry.Option) == 0)
                continue;
            if (length == 0)
                first = entry.Name;
            else
                length++;
            length += entry.Name.Bytes.Length;
        }
        if (length == first.Bytes.Length)
            return first;

        ownedBytes = MemoryCapacity.ArrayBackingBytes<byte>(length);
        if (memory is { HasCommand: true })
            memory.PrepareRetainedStorage(ownedBytes);
        var bytes = new byte[length];
        var offset = 0;
        foreach (var entry in entries)
        {
            if ((enabled & entry.Option) == 0)
                continue;
            if (offset != 0)
                bytes[offset++] = (byte)':';
            entry.Name.Bytes.Span.CopyTo(bytes.AsSpan(offset));
            offset += entry.Name.Bytes.Length;
        }
        return ShellValue.FromBytes(bytes);
    }

    private static ShellOption CollectBashFlags()
    {
        var options = ShellOption.None;
        foreach (var entry in BashOptions)
            options |= entry.Option;
        return options;
    }
}
