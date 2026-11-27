using System;
using System.Collections.Generic;

namespace Lokad.Bash.Helpers;

/// <summary>Tokenizes byte-valued command arguments without decoding their contents.</summary>
/// <remarks>
/// Supports grouped short options, long options with optional inline values, and
/// the <c>--</c> terminator. Positional arguments do not stop option parsing.
/// Commands consume required values explicitly through raw reads or group remainders.
/// </remarks>
public sealed class CommandLineOptionParser
{
    private readonly IReadOnlyList<ShellValue> _arguments;
    private readonly bool _allowSingleDashLongOptions;
    private bool _parsingOptions = true;
    private int _argumentIndex;
    private ReadOnlyMemory<byte> _shortGroup;

    /// <summary>Retains the finished argument list; callers must not mutate it.</summary>
    /// <param name="arguments">Already-expanded argument bytes.</param>
    /// <param name="allowSingleDashLongOptions">Treat tokens longer than two bytes, such as <c>-name</c>, as long options.</param>
    public CommandLineOptionParser(IReadOnlyList<ShellValue> arguments, bool allowSingleDashLongOptions)
    {
        _arguments = arguments;
        _allowSingleDashLongOptions = allowSingleDashLongOptions;
    }

    /// <summary>Reads the next option or positional argument, or returns false when exhausted.</summary>
    public bool TryRead(out CommandLineOptionItem item) => TryRead([], out item);

    /// <summary>
    /// Reads the next item, recognizing the supplied single-dash long names before short groups.
    /// Names are byte-valued and exclude the leading dash and any inline value.
    /// </summary>
    public bool TryRead(ReadOnlySpan<ReadOnlyMemory<byte>> singleDashLongOptions, out CommandLineOptionItem item)
    {
        if (TryReadShortGroup(out item))
            return true;

        while (_argumentIndex < _arguments.Count)
        {
            var argument = _arguments[_argumentIndex++];
            var bytes = argument.Bytes;
            if (_parsingOptions && bytes.Span.SequenceEqual("--"u8))
            {
                _parsingOptions = false;
                continue;
            }

            if (_parsingOptions && bytes.Length > 1 && bytes.Span[0] == (byte)'-')
            {
                var doubleDash = bytes.Span[1] == (byte)'-';
                var longOption = doubleDash || (_allowSingleDashLongOptions && bytes.Length > 2);
                if (!longOption && bytes.Length > 2)
                {
                    var name = bytes.Span[1..];
                    var equalsIndex = name.IndexOf((byte)'=');
                    if (equalsIndex >= 0)
                        name = name[..equalsIndex];
                    foreach (var knownName in singleDashLongOptions)
                    {
                        if (!name.SequenceEqual(knownName.Span))
                            continue;
                        longOption = true;
                        break;
                    }
                }
                if (longOption)
                {
                    item = ParseLongOption(bytes[(doubleDash ? 2 : 1)..]);
                    return true;
                }

                _shortGroup = bytes[1..];
                return TryReadShortGroup(out item);
            }

            item = CommandLineOptionItem.Argument(argument);
            return true;
        }

        item = default;
        return false;

        static CommandLineOptionItem ParseLongOption(ReadOnlyMemory<byte> option)
        {
            var equalsIndex = option.Span.IndexOf((byte)'=');
            return equalsIndex < 0
                ? CommandLineOptionItem.LongOption(ShellValue.FromBytes(option), ShellValue.Empty, false)
                : CommandLineOptionItem.LongOption(
                    ShellValue.FromBytes(option[..equalsIndex]),
                    ShellValue.FromBytes(option[(equalsIndex + 1)..]),
                    true);
        }

        bool TryReadShortGroup(out CommandLineOptionItem item)
        {
            if (_shortGroup.IsEmpty)
            {
                item = default;
                return false;
            }

            item = CommandLineOptionItem.ShortOption(_shortGroup.Span[0]);
            _shortGroup = _shortGroup[1..];
            return true;
        }
    }

    /// <summary>
    /// Reads the next argument verbatim, discarding any pending short-group remainder.
    /// A raw <c>--</c> is data and does not change subsequent option parsing.
    /// </summary>
    public bool TryReadRaw(out ShellValue argument)
    {
        _shortGroup = ReadOnlyMemory<byte>.Empty;
        if (_argumentIndex >= _arguments.Count)
        {
            argument = ShellValue.Empty;
            return false;
        }

        argument = _arguments[_argumentIndex++];
        return true;
    }

    /// <summary>Consumes the remaining bytes in the current short group, if any.</summary>
    public bool TryReadShortGroupRemainder(out ShellValue remainder)
    {
        remainder = ShellValue.FromBytes(_shortGroup);
        _shortGroup = ReadOnlyMemory<byte>.Empty;
        return !remainder.IsEmpty;
    }

}

/// <summary>
/// Represents a parsed command-line option or positional argument.
/// </summary>
public readonly struct CommandLineOptionItem
{
    /// <summary>
    /// Initializes a parsed option or argument item.
    /// </summary>
    private CommandLineOptionItem(
        CommandLineOptionKind kind,
        byte shortName,
        ShellValue longName,
        ShellValue value,
        bool hasValue,
        ShellValue argument)
    {
        Kind = kind;
        ShortName = shortName;
        LongName = longName;
        Value = value;
        HasValue = hasValue;
        ArgumentValue = argument;
    }

    /// <summary>
    /// Gets the parsed item kind.
    /// </summary>
    public CommandLineOptionKind Kind { get; }

    /// <summary>
    /// Gets the short option name when <see cref="Kind"/> is <see cref="CommandLineOptionKind.ShortOption"/>.
    /// </summary>
    public byte ShortName { get; }

    /// <summary>
    /// Gets the long option name when <see cref="Kind"/> is <see cref="CommandLineOptionKind.LongOption"/>.
    /// </summary>
    public ShellValue LongName { get; }

    /// <summary>
    /// Gets the inline option value when present.
    /// </summary>
    public ShellValue Value { get; }

    /// <summary>
    /// Gets whether an inline value was present.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>
    /// Gets the positional argument when <see cref="Kind"/> is <see cref="CommandLineOptionKind.Argument"/>.
    /// </summary>
    public ShellValue ArgumentValue { get; }

    /// <summary>
    /// Creates a parsed short option item.
    /// </summary>
    public static CommandLineOptionItem ShortOption(byte name)
    {
        return new CommandLineOptionItem(CommandLineOptionKind.ShortOption, name, ShellValue.Empty,
            ShellValue.Empty, false, ShellValue.Empty);
    }

    /// <summary>
    /// Creates a parsed long option item.
    /// </summary>
    public static CommandLineOptionItem LongOption(
        ShellValue name,
        ShellValue value,
        bool hasValue)
    {
        return new CommandLineOptionItem(CommandLineOptionKind.LongOption, 0, name, value, hasValue,
            ShellValue.Empty);
    }

    /// <summary>
    /// Creates a parsed positional argument item.
    /// </summary>
    public static CommandLineOptionItem Argument(ShellValue value)
    {
        return new CommandLineOptionItem(CommandLineOptionKind.Argument, 0, ShellValue.Empty,
            ShellValue.Empty, false, value);
    }
}

/// <summary>
/// Enumerates the kinds of parsed command-line items.
/// </summary>
public enum CommandLineOptionKind
{
    /// <summary>
    /// A short option, such as <c>-n</c>.
    /// </summary>
    ShortOption,
    /// <summary>
    /// A long option, such as <c>--exclude=dir</c>.
    /// </summary>
    LongOption,
    /// <summary>
    /// A positional argument.
    /// </summary>
    Argument
}
