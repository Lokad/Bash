using System;
using System.Collections.Generic;
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
    Pipefail = 1024
}

internal static class ShellOptions
{
    internal const ShellOption Fixed = ShellOption.BraceExpand | ShellOption.Pipefail;
    internal const ShellOption Default = Fixed | ShellOption.GlobSkipDots;
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
        new(ShellValue.FromText("globskipdots"), ShellOption.GlobSkipDots),
        new(ShellValue.FromText("inherit_errexit"), ShellOption.InheritErrexit),
        new(ShellValue.FromText("localvar_inherit"), ShellOption.LocalVariableInheritance),
        new(ShellValue.FromText("nocaseglob"), ShellOption.NoCaseGlob),
        new(ShellValue.FromText("nocasematch"), ShellOption.NoCaseMatch),
        new(ShellValue.FromText("nullglob"), ShellOption.Nullglob)
    ];

    private static readonly ShellOption BashMask = CollectBashFlags();
    private static readonly ShellValue[] SetValues =
        [Format(SetOptions, Fixed, memory: null, out _), Format(SetOptions, Fixed | ShellOption.Errexit, memory: null, out _)];

    internal static ShellValue DefaultBashBinding { get; } = FormatBashBinding(SelectBashFlags(Default), memory: null, out _);

    internal static IReadOnlyList<Entry> Names(bool setOptions) => setOptions ? SetOptions : BashOptions;

    internal static ShellOption Find(ReadOnlySpan<byte> name, bool setOptions)
    {
        foreach (var entry in setOptions ? SetOptions : BashOptions)
            if (name.SequenceEqual(entry.Name.Bytes.Span))
                return entry.Option;
        return ShellOption.None;
    }

    internal static ShellValue SetBinding(ShellOption enabled) => SetValues[(int)(enabled & ShellOption.Errexit)];

    internal static ShellOption SelectBashFlags(ShellOption enabled) => enabled & BashMask;

    internal static ShellValue FormatBashBinding(ShellOption enabled, EvaluationMemory? memory, out long ownedBytes) =>
        Format(BashOptions, enabled, memory, out ownedBytes);

    internal static ShellOption ParseImported(ReadOnlySpan<byte> value, bool setOptions)
    {
        var enabled = ShellOption.None;
        foreach (var component in value.Split((byte)':'))
            enabled |= Find(value[component], setOptions);
        return enabled;
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
