using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>
    /// Resolves command-control builtins by rewriting their target into the normal command path.
    /// </summary>
    /// <remarks>
    /// Wrapper targets share ordinary command-field selection; lookup, completion and redirection lifetime stay separate.
    /// </remarks>
    private async Task<PreparedCommand> PrepareCommandAsync(
        SimpleCommand simpleCommand,
        ShellValue commandValue,
        ScopedEvaluation scope)
    {
        var lookupMode = CommandLookupMode.Normal;
        ExecOptions? executionOptions = null;
        var completion = CommandCompletion.Continue;
        var allowsPersistentRedirections = true;
        // Keep source words and surplus expansion fields separate; wrappers consume them without rebuilding the suffix.
        var wordIndex = 1;
        IReadOnlyList<ShellValue> fields = [];
        var fieldIndex = 0;
        // Byte-valued function names need no text allocation. Other names use the shared decode budget.
        var commandName = _state.Functions.ContainsKey(commandValue) ? string.Empty
            : await DecodeExpandedValueAsync(commandValue).ConfigureAwait(false);

        while (BuiltinClassification.TryResolve(commandName.AsSpan(), out var builtinKind))
        {
            if (lookupMode == CommandLookupMode.Normal
                && _state.Functions.ContainsKey(commandValue))
                break;

            if (builtinKind is not (BuiltinKind.Builtin or BuiltinKind.Command or BuiltinKind.Exec))
                break;

            var next = await ReadFieldAsync().ConfigureAwait(false);
            if (builtinKind == BuiltinKind.Builtin)
            {
                allowsPersistentRedirections = false;
                if (next is not { } first)
                    return Terminal(0, ShellValue.Empty, CommandMessageChannel.StandardError, builtinKind);
                if (first.Bytes.Span.SequenceEqual("--help"u8))
                    return Help(builtinKind);
                if (first.Bytes.Span.SequenceEqual("--"u8))
                    next = await ReadFieldAsync().ConfigureAwait(false);
                else if (first.Bytes.Length > 1 && first.Bytes.Span[0] == (byte)'-')
                    return InvalidOption(builtinKind, first.Bytes.Span[1]);

                if (next is not { } name)
                    return Terminal(0, ShellValue.Empty, CommandMessageChannel.StandardError, builtinKind);
                Memory.Charge(checked(4L * name.Bytes.Length));
                if (!name.TryGetUtf8Text(out var builtinName)
                    || !BuiltinClassification.TryResolve(builtinName.AsSpan(), out _))
                {
                    Memory.Charge(checked(4L * ("builtin: "u8.Length + (long)name.Bytes.Length + ": not a shell builtin\n"u8.Length)));
                    return Terminal(1, ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0)
                        .Append("builtin: "u8).Append(name.Bytes).Append(": not a shell builtin\n"u8).ToMemory()),
                        CommandMessageChannel.StandardError, builtinKind);
                }

                commandName = builtinName;
                commandValue = name;
                lookupMode = CommandLookupMode.BuiltinOnly;
                continue;
            }

            CommandQueryOutputMode? outputMode = null;
            ShellValue? argumentZero = null;
            var login = false;
            var clearEnvironment = false;
            while (next is { } first)
            {
                if (first.Bytes.Span.SequenceEqual("--help"u8))
                    return Help(builtinKind);
                if (first.Bytes.Span.SequenceEqual("--"u8))
                {
                    next = await ReadFieldAsync().ConfigureAwait(false);
                    break;
                }

                if (first.Bytes.Length < 2 || first.Bytes.Span[0] != (byte)'-')
                    break;

                var options = new CommandLineOptionParser([first], allowSingleDashLongOptions: false);
                while (options.TryRead(out var option))
                {
                    var flag = option.Kind == CommandLineOptionKind.ShortOption ? option.ShortName : (byte)'-';
                    switch (builtinKind, flag)
                    {
                        case (BuiltinKind.Command, (byte)'p'):
                            // The controlled host already uses a fixed registry, independent of PATH.
                            break;
                        case (BuiltinKind.Command, (byte)'v'):
                            outputMode = CommandQueryOutputMode.CommandName;
                            break;
                        case (BuiltinKind.Command, (byte)'V'):
                            outputMode = CommandQueryOutputMode.TypeDescription;
                            break;
                        case (BuiltinKind.Exec, (byte)'c'):
                            clearEnvironment = true;
                            break;
                        case (BuiltinKind.Exec, (byte)'l'):
                            login = true;
                            break;
                        case (BuiltinKind.Exec, (byte)'a'):
                            if (options.TryReadShortGroupRemainder(out var attachedName))
                            {
                                argumentZero = attachedName;
                                break;
                            }

                            argumentZero = await ReadFieldAsync().ConfigureAwait(false);
                            if (!argumentZero.HasValue)
                                return OptionFailure(builtinKind, ShellValue.FromText("-a: option requires an argument"));
                            break;
                        default:
                            return InvalidOption(builtinKind, flag);
                    }
                }

                next = await ReadFieldAsync().ConfigureAwait(false);
            }

            if (outputMode.HasValue)
            {
                var operands = RemainingCommand(next).Words;
                var names = new List<ShellValue>(operands.Count);
                foreach (var word in operands)
                {
                    var firstName = names.Count;
                    if (word.Parts is [LiteralWordPart { Kind: WordPartKind.SingleQuoted } literal])
                    {
                        // Option scanning may already have captured this field; retain it without another byte copy.
                        Memory.Charge(256);
                        names.Add(literal.Value);
                    }
                    else
                        names.AddRange(await ExpandToWordsAsync(word, scope).ConfigureAwait(false));
                    if (_state.HasPendingExpansionError)
                        break;
                    // Reserve decoding before later operand effects; malformed bytes need no text copy.
                    for (var nameIndex = firstName; nameIndex < names.Count; nameIndex++)
                        if (System.Text.Unicode.Utf8.IsValid(names[nameIndex].Bytes.Span))
                            Memory.Charge(checked(4L * ((long)names[nameIndex].Bytes.Length + 1)));
                }

                return new PreparedCommand.Query(
                    names,
                    outputMode.Value,
                    executionOptions,
                    completion,
                    RedirectionLifetime.Command);
            }

            if (next is not { } target)
                return Terminal(0, ShellValue.Empty, CommandMessageChannel.StandardError, builtinKind);

            commandValue = target;
            commandName = await DecodeExpandedValueAsync(commandValue).ConfigureAwait(false);
            lookupMode = CommandLookupMode.BypassFunctions;
            if (builtinKind == BuiltinKind.Exec)
            {
                // A nested virtual exec selects a new name, but cannot restore already-cleared exports.
                executionOptions = new ExecOptions(
                    argumentZero, login, clearEnvironment || executionOptions?.ClearEnvironment == true);
                completion = CommandCompletion.ExitEvaluation;
            }
        }

        return new PreparedCommand.Executable(
            wordIndex == 1 && fields.Count == 0 ? simpleCommand : RemainingCommand(commandValue),
            commandName,
            ResolveCommandName(commandValue, commandName, lookupMode),
            executionOptions,
            completion,
            RedirectionLifetime.Command);

        PreparedCommand.Terminal InvalidOption(BuiltinKind kind, byte option) =>
            OptionFailure(kind, ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(32)
                .Append((byte)'-').Append(option).Append(": invalid option"u8).ToMemory()));

        PreparedCommand.Terminal OptionFailure(BuiltinKind kind, ShellValue message)
        {
            var usageBytes = Utf8Text.Encode(BuiltinDocumentation.For(kind).Synopsis(commandName));
            Memory.Charge(checked(4L * (2L * commandName.Length + message.Bytes.Length + usageBytes.Length
                + ": "u8.Length + ": usage: "u8.Length + 2)));
            var output = ByteMemoryBuilder.CreateUnaccounted(0);
            output.Append(Utf8Text.Encode(commandName)).Append(": "u8).Append(message.Bytes).Append((byte)'\n')
                .Append(Utf8Text.Encode(commandName)).Append(": usage: "u8).Append(usageBytes).Append((byte)'\n');
            return Terminal(2, ShellValue.FromBytes(output.ToMemory()), CommandMessageChannel.StandardError, kind);
        }

        PreparedCommand.Terminal Help(BuiltinKind kind)
        {
            // Describe the hosted subset; do not promise Bash process replacement or execfail support.
            var documentation = BuiltinDocumentation.For(kind);
            var message = ShellValue.FromText($": {documentation.Synopsis(commandName)}\n    {documentation.Description}\n");
            Memory.Charge(checked(4L * ((long)commandName.Length + message.Bytes.Length)));
            return Terminal(2, ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(128).Append(Utf8Text.Encode(commandName))
                .Append(message.Bytes).ToMemory()), CommandMessageChannel.StandardOutput, kind);
        }

        PreparedCommand.Terminal Terminal(int exitCode, ShellValue message, CommandMessageChannel channel, BuiltinKind kind) =>
            new(EvaluationResult.FromExitCode(exitCode), message, channel, RemainingCommand(null), completion,
                // Direct exec retains redirections even on option failure/help; builtin exec restores them.
                kind == BuiltinKind.Exec && allowsPersistentRedirections
                    ? RedirectionLifetime.Evaluation : RedirectionLifetime.Command);

        async Task<ShellValue?> ReadFieldAsync()
        {
            while (!_state.HasPendingExpansionError)
            {
                if (fieldIndex < fields.Count)
                    return fields[fieldIndex++];
                if (wordIndex >= simpleCommand.Words.Count)
                    break;
                var word = simpleCommand.Words[wordIndex++];
                if (word.Parts.Count == 1 && word.Parts[0].Kind == WordPartKind.SingleQuoted)
                    return word.Parts[0].GetUnexpandedValue();
                fields = await ExpandToWordsAsync(word, scope).ConfigureAwait(false);
                fieldIndex = 0;
            }
            return null;
        }

        SimpleCommand RemainingCommand(ShellValue? first)
        {
            var words = new List<Word>((first.HasValue ? 1 : 0)
                + fields.Count - fieldIndex + simpleCommand.Words.Count - wordIndex);
            if (first is { } value)
                words.Add(new Word([WordPart.FromValue(WordPartKind.SingleQuoted, value)]));
            for (var index = fieldIndex; index < fields.Count; index++)
                words.Add(new Word([WordPart.FromValue(WordPartKind.SingleQuoted, fields[index])]));
            for (var index = wordIndex; index < simpleCommand.Words.Count; index++)
                words.Add(simpleCommand.Words[index]);
            return simpleCommand with { Words = words };
        }
    }

}
