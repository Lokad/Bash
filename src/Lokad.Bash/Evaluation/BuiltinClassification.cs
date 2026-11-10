using System;
using System.Collections.Generic;
using System.Linq;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Identifies shell builtins known to command resolution, including host-backed builtins.
/// </summary>
internal enum BuiltinKind
{
    Colon,
    True,
    False,
    Exit,
    Break,
    Continue,
    Return,
    Let,
    Test,
    Pwd,
    Cd,
    Echo,
    Printf,
    Read,
    Unset,
    Dirs,
    Pushd,
    Popd,
    Export,
    Readonly,
    Mapfile,
    Hash,
    Shift,
    Set,
    Exec,
    Declare,
    Local,
    Caller,
    Source,
    Eval,
    Getopts,
    ApplyPatch,
    Command,
    Type,
    Builtin,
    Help,
    Trap
}

/// <summary>
/// Provides the authoritative mapping from command names to shell builtins.
/// </summary>
internal static class BuiltinClassification
{
    private static readonly Dictionary<string, BuiltinKind> Kinds = new(StringComparer.Ordinal)
    {
        [":"] = BuiltinKind.Colon,
        ["true"] = BuiltinKind.True,
        ["false"] = BuiltinKind.False,
        ["exit"] = BuiltinKind.Exit,
        ["break"] = BuiltinKind.Break,
        ["continue"] = BuiltinKind.Continue,
        ["return"] = BuiltinKind.Return,
        ["let"] = BuiltinKind.Let,
        ["test"] = BuiltinKind.Test,
        ["["] = BuiltinKind.Test,
        ["pwd"] = BuiltinKind.Pwd,
        ["cd"] = BuiltinKind.Cd,
        ["echo"] = BuiltinKind.Echo,
        ["printf"] = BuiltinKind.Printf,
        ["read"] = BuiltinKind.Read,
        ["unset"] = BuiltinKind.Unset,
        ["dirs"] = BuiltinKind.Dirs,
        ["pushd"] = BuiltinKind.Pushd,
        ["popd"] = BuiltinKind.Popd,
        ["export"] = BuiltinKind.Export,
        ["readonly"] = BuiltinKind.Readonly,
        ["mapfile"] = BuiltinKind.Mapfile,
        ["readarray"] = BuiltinKind.Mapfile,
        ["hash"] = BuiltinKind.Hash,
        ["shift"] = BuiltinKind.Shift,
        ["set"] = BuiltinKind.Set,
        ["exec"] = BuiltinKind.Exec,
        ["declare"] = BuiltinKind.Declare,
        ["typeset"] = BuiltinKind.Declare,
        ["local"] = BuiltinKind.Local,
        ["caller"] = BuiltinKind.Caller,
        ["source"] = BuiltinKind.Source,
        ["."] = BuiltinKind.Source,
        ["eval"] = BuiltinKind.Eval,
        ["getopts"] = BuiltinKind.Getopts,
        ["apply_patch"] = BuiltinKind.ApplyPatch,
        ["command"] = BuiltinKind.Command,
        ["type"] = BuiltinKind.Type,
        ["builtin"] = BuiltinKind.Builtin,
        ["help"] = BuiltinKind.Help,
        ["trap"] = BuiltinKind.Trap
    };

    /// <summary>Evaluator builtin topics from command resolution; host commands provide their own help.</summary>
    public static IReadOnlyList<BuiltinRegistration> Entries { get; } =
        Kinds.Where(static pair => pair.Value != BuiltinKind.ApplyPatch)
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new BuiltinRegistration(pair.Key, pair.Value)).ToArray();

    public static bool TryResolve(ReadOnlySpan<char> word, out BuiltinKind kind) =>
        Kinds.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(word, out kind);

    public static bool TryGetFixedExitCode(BuiltinKind kind, out int exitCode)
    {
        if (kind is BuiltinKind.Colon or BuiltinKind.True)
        {
            exitCode = 0;
            return true;
        }

        if (kind == BuiltinKind.False)
        {
            exitCode = 1;
            return true;
        }

        exitCode = 0;
        return false;
    }
}

/// <summary>A classified builtin name, with its UTF-8 spelling retained for help pattern matching.</summary>
internal readonly record struct BuiltinRegistration(string Name, BuiltinKind Kind)
{
    public ReadOnlyMemory<byte> NameBytes { get; } = Utf8Text.Encode(Name);
}
