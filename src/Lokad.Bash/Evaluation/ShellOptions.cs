using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

[Flags]
internal enum ShellOption
{
    None = 0,
    // Keep the frequently reached set options in the low bits for direct projection lookup.
    Errexit = 1,
    Noglob = 2,
    Nounset = 4,
    BraceExpand = 8,
    Allexport = 16,
    Nullglob = 32,
    NoCaseGlob = 64,
    NoCaseMatch = 128,
    GlobSkipDots = 256,
    Dotglob = 512,
    Pipefail = 1024,
    InheritErrexit = 2048,
    XpgEcho = 4096,
    SourcePath = 8192,
    ShiftVerbose = 16384,
    LocalVariableInheritance = 32768,
    PatsubReplacement = 65536,
    Failglob = 131072,
    Globstar = 262144
}

internal static class ShellOptions
{
    internal const ShellOption Fixed = ShellOption.Pipefail;
    private const ShellOption DefaultBashFlags = ShellOption.GlobSkipDots | ShellOption.PatsubReplacement | ShellOption.SourcePath;
    internal const ShellOption Default = Fixed | ShellOption.BraceExpand | DefaultBashFlags;
    internal static readonly BindingName BashOptionsName = "BASHOPTS";
    internal static readonly BindingName SetOptionsName = "SHELLOPTS";

    internal readonly record struct Entry(ShellValue Name, ShellOption Option, byte ShortFlag)
    {
        internal Entry(ShellValue name, ShellOption option) : this(name, option, 0) { }
    }

    private static readonly Entry[] SetOptions =
    [
        new(ShellValue.FromText("allexport"), ShellOption.Allexport, (byte)'a'),
        new(ShellValue.FromText("braceexpand"), ShellOption.BraceExpand, (byte)'B'),
        new(ShellValue.FromText("errexit"), ShellOption.Errexit, (byte)'e'),
        new(ShellValue.FromText("noglob"), ShellOption.Noglob, (byte)'f'),
        new(ShellValue.FromText("nounset"), ShellOption.Nounset, (byte)'u'),
        new(ShellValue.FromText("pipefail"), ShellOption.Pipefail)
    ];

    private static readonly Entry[] BashOptions =
    [
        new(ShellValue.FromText("dotglob"), ShellOption.Dotglob),
        new(ShellValue.FromText("failglob"), ShellOption.Failglob),
        new(ShellValue.FromText("globskipdots"), ShellOption.GlobSkipDots),
        new(ShellValue.FromText("globstar"), ShellOption.Globstar),
        new(ShellValue.FromText("inherit_errexit"), ShellOption.InheritErrexit),
        new(ShellValue.FromText("localvar_inherit"), ShellOption.LocalVariableInheritance),
        new(ShellValue.FromText("nocaseglob"), ShellOption.NoCaseGlob),
        new(ShellValue.FromText("nocasematch"), ShellOption.NoCaseMatch),
        new(ShellValue.FromText("nullglob"), ShellOption.Nullglob),
        new(ShellValue.FromText("patsub_replacement"), ShellOption.PatsubReplacement),
        new(ShellValue.FromText("shift_verbose"), ShellOption.ShiftVerbose),
        new(ShellValue.FromText("sourcepath"), ShellOption.SourcePath),
        new(ShellValue.FromText("xpg_echo"), ShellOption.XpgEcho)
    ];

    private static readonly ShellOption BashMask = CollectFlags(BashOptions);
    private static readonly (byte[] Order, SearchValues<byte> Flags) ShortSet = BuildShortSet();
    internal static readonly ShellOption MutableSet = CollectFlags(SetOptions) & ~Fixed;
    internal static readonly ShellOption LocalSnapshot = MutableSet | ShellOption.InheritErrexit | ShellOption.ShiftVerbose
        | ShellOption.SourcePath;
    internal static SearchValues<byte> ShortSetFlags => ShortSet.Flags;

    internal static ShellValue DefaultBashBinding { get; } = Format(BashOptions, DefaultBashFlags, memory: null, out _);

    internal static IReadOnlyList<Entry> Names(bool setOptions) => setOptions ? SetOptions : BashOptions;

    internal static ShellOption Find(ReadOnlySpan<byte> name, bool setOptions)
    {
        foreach (var entry in setOptions ? SetOptions : BashOptions)
            if (name.SequenceEqual(entry.Name.Bytes.Span))
                return entry.Option;
        return ShellOption.None;
    }

    internal static ShellOption FindShortSetOption(byte flag)
    {
        if (flag != 0)
            foreach (var entry in SetOptions)
                if (entry.ShortFlag == flag)
                    return entry.Option;
        return ShellOption.None;
    }

    internal static ShellValue SetBinding(ShellOption enabled) =>
        ReadProjection(ref _setBindings, enabled, ProjectionKind.Binding);

    internal static ShellValue Flags(ShellOption enabled) =>
        ReadProjection(ref _shortFlags, enabled, ProjectionKind.Flags);

    internal static ShellValue InspectSetOptions(ShellOption enabled, bool listing) => listing
        ? ReadProjection(ref _setListings, enabled, ProjectionKind.Listing)
        : ReadProjection(ref _setCommands, enabled, ProjectionKind.Commands);

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

    private enum ProjectionKind { Binding, Flags, Listing, Commands }
    private sealed record Projection(ShellValue Value);
    private const int ProjectionCapacity = 32;
    private static Projection?[]? _setBindings, _shortFlags, _setListings, _setCommands;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ShellValue ReadProjection(ref Projection?[]? slots, ShellOption enabled, ProjectionKind kind)
    {
        enabled &= MutableSet;
        var index = (int)enabled;
        var cache = Volatile.Read(ref slots);
        if (cache != null && (uint)index < ProjectionCapacity && Volatile.Read(ref cache[index]) is { } found)
            return found.Value;
        return PublishProjection(ref slots, cache, enabled, kind);
    }

    private static ShellValue PublishProjection(ref Projection?[]? slots, Projection?[]? cache,
        ShellOption enabled, ProjectionKind kind)
    {
        var index = (int)enabled;
        // Any additional bits outside the fixed cache use the same formatter on demand.
        if ((uint)index >= ProjectionCapacity)
            return RenderProjection(enabled, kind);
        if (cache == null)
        {
            cache = new Projection?[ProjectionCapacity];
            cache = Interlocked.CompareExchange(ref slots, cache, null) ?? cache;
        }
        ref var slot = ref cache[index];
        var found = Volatile.Read(ref slot);
        if (found != null)
            return found.Value;
        var candidate = new Projection(RenderProjection(enabled, kind));
        return (Interlocked.CompareExchange(ref slot, candidate, null) ?? candidate).Value;
    }

    private static ShellValue RenderProjection(ShellOption enabled, ProjectionKind kind) => kind switch
    {
        ProjectionKind.Binding => Format(SetOptions, Fixed | enabled, memory: null, out _),
        ProjectionKind.Flags => FormatShortFlags(enabled),
        _ => FormatInspection(enabled, listing: kind == ProjectionKind.Listing)
    };

    private static ShellValue FormatShortFlags(ShellOption enabled)
    {
        Span<byte> flags = stackalloc byte[ShortSet.Order.Length];
        var count = 0;
        foreach (var flag in ShortSet.Order)
            if ((enabled & FindShortSetOption(flag)) != 0)
                flags[count++] = flag;
        // Keep option reporting independent of the general single-byte value cache.
        return count == 0 ? ShellValue.Empty : ShellValue.FromBytes(flags[..count].ToArray());
    }

    private static ShellValue FormatInspection(ShellOption enabled, bool listing)
    {
        var length = 0;
        const int commandDecorationBytes = 7 + 1;
        foreach (var entry in SetOptions)
            if ((entry.Option & MutableSet) != 0)
                length += entry.Name.Bytes.Length + (listing ? ListingEnd(enabled, entry.Option).Length : commandDecorationBytes);

        var bytes = new byte[length];
        var remaining = bytes.AsSpan();
        foreach (var entry in SetOptions)
        {
            if ((entry.Option & MutableSet) == 0)
                continue;
            if (!listing)
                remaining = Copy((enabled & entry.Option) != 0 ? "set -o "u8 : "set +o "u8, remaining);
            remaining = Copy(entry.Name.Bytes.Span, remaining);
            remaining = Copy(listing ? ListingEnd(enabled, entry.Option) : "\n"u8, remaining);
        }
        return ShellValue.FromBytes(bytes);

        static ReadOnlySpan<byte> ListingEnd(ShellOption flags, ShellOption option) =>
            (flags & option) != 0 ? "\ton\n"u8 : "\toff\n"u8;

        static Span<byte> Copy(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            source.CopyTo(destination);
            return destination[source.Length..];
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

    private static (byte[] Order, SearchValues<byte> Flags) BuildShortSet()
    {
        Span<byte> flags = stackalloc byte[SetOptions.Length];
        var count = 0;
        // Swap the ASCII case bits while sorting to put lowercase flags first.
        foreach (var entry in SetOptions)
            if (entry.ShortFlag != 0)
                flags[count++] = (byte)(entry.ShortFlag ^ 0x20);
        var order = flags[..count];
        order.Sort();
        for (var index = 0; index < order.Length; index++)
            order[index] ^= 0x20;
        return (order.ToArray(), SearchValues.Create(order));
    }

    private static ShellOption CollectFlags(Entry[] entries)
    {
        var options = ShellOption.None;
        foreach (var entry in entries)
            options |= entry.Option;
        return options;
    }
}
