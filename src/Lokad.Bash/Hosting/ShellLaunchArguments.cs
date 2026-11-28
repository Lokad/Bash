using System;
using System.Collections.Generic;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash;

/// <summary>Selects the reader and options for the supported same-host launch forms.</summary>
internal readonly record struct ShellLaunchArguments(
    int ReaderIndex, bool CommandString, ShellOption EnabledOptions,
    ShellLaunchError Error, ShellValue ErrorOperand)
{
    internal static ShellLaunchArguments Parse(IReadOnlyList<ShellValue> arguments)
    {
        var index = 0;
        var commandString = false;
        var enabledOptions = ShellOptions.Default;
        while (index < arguments.Count)
        {
            var group = arguments[index].Bytes.Span;
            if (group.SequenceEqual("--"u8) || group.SequenceEqual("-"u8))
            {
                index++;
                break;
            }
            if (group.Length < 2 || group[0] is not ((byte)'-' or (byte)'+')
                || group[1] is not ((byte)'e' or (byte)'O' or (byte)'c'))
                break;

            var enable = group[0] == (byte)'-';
            var spelling = arguments[index++];
            foreach (var flag in group[1..])
            {
                if (flag == (byte)'c')
                {
                    commandString = true;
                    continue;
                }

                ShellOption option;
                if (flag == (byte)'e')
                    option = ShellOption.Errexit;
                else if (flag == (byte)'O')
                {
                    if (index == arguments.Count)
                        return Failure(ShellLaunchError.MissingOptionName, spelling);
                    var name = arguments[index++];
                    option = ShellOptions.Find(name.Bytes.Span, setOptions: false);
                    if (option == ShellOption.None)
                        return Failure(ShellLaunchError.InvalidOptionName, name);
                }
                else
                    return Failure(ShellLaunchError.InvalidFlag, spelling);

                enabledOptions = enable ? enabledOptions | option : enabledOptions & ~option;
            }
        }

        return new ShellLaunchArguments(index, commandString, enabledOptions, ShellLaunchError.None, ShellValue.Empty);

        static ShellLaunchArguments Failure(ShellLaunchError error, ShellValue operand) =>
            new(0, false, ShellOptions.Default, error, operand);
    }
}

internal enum ShellLaunchError { None, MissingOptionName, InvalidOptionName, InvalidFlag }
