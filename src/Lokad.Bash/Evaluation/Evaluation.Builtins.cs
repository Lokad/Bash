using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
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
        IReadOnlyList<Word> sourceWords,
        ShellValue commandValue,
        ScopedEvaluation scope)
    {
        var lookupMode = CommandLookupMode.Normal;
        ExecOptions? executionOptions = null;
        var completion = CommandCompletion.Continue;
        var allowsPersistentRedirections = true;
        // Keep source words and surplus expansion fields separate; wrappers consume them without rebuilding the suffix.
        var wordIndex = 1;
        // Byte-valued function names need no text allocation. Other names use the shared decode budget.
        var commandName = _state.Functions.ContainsKey(commandValue) ? string.Empty
            : await DecodeExpandedValueAsync(commandValue).ConfigureAwait(false);
        if (!BuiltinClassification.TryResolve(commandName.AsSpan(), out var initialBuiltin)
            || initialBuiltin is not (BuiltinKind.Builtin or BuiltinKind.Command or BuiltinKind.Exec))
            return Executable(new CommandWords(sourceWords));

        using var fieldMemory = new MemoryCapacity(Memory.Budget, 0);
        var fields = new List<ShellValue>();
        var fieldIndex = 0;

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
                if (!BuiltinClassification.TryResolve(name.Bytes.Span, out _))
                {
                    return Terminal(1, BuildTerminalMessage(
                        [ShellValue.FromText("builtin: "), name, ShellValue.FromText(": not a shell builtin\n")]),
                        CommandMessageChannel.StandardError, builtinKind);
                }

                // The registry only accepts short ASCII names.
                Memory.Charge(checked(4L * name.Bytes.Length));
                commandName = name.ToUtf8String();
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
                                return OptionFailure(builtinKind, ShellValue.FromText("-a"), ": option requires an argument");
                            break;
                        default:
                            return InvalidOption(builtinKind, flag);
                    }
                }

                next = await ReadFieldAsync().ConfigureAwait(false);
            }

            if (outputMode.HasValue)
            {
                var names = new List<ShellValue>();
                Memory.PrepareListCapacity(names, checked((next.HasValue ? 1 : 0) + fields.Count - fieldIndex));
                if (next is { } firstName)
                    AddCapturedName(firstName);
                for (var index = fieldIndex; index < fields.Count; index++)
                    AddCapturedName(fields[index]);
                fields.Clear();
                fieldMemory.Dispose();

                for (var index = wordIndex; index < sourceWords.Count && !_state.HasPendingExpansionError; index++)
                {
                    var word = sourceWords[index];
                    if (word.Parts is [LiteralWordPart { Kind: WordPartKind.SingleQuoted } literal])
                    {
                        AddCapturedName(literal.Value);
                        continue;
                    }

                    var firstAdded = names.Count;
                    await AppendOperandValuesAsync(word, scope, names).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        break;
                    // Reserve decoding before later operand effects; malformed bytes need no text copy.
                    for (var nameIndex = firstAdded; nameIndex < names.Count; nameIndex++)
                        ReserveNameDecoding(names[nameIndex]);
                }

                return new PreparedCommand.Query(
                    names,
                    outputMode.Value,
                    executionOptions,
                    completion,
                    RedirectionLifetime.Command);

                void AddCapturedName(ShellValue name)
                {
                    AddOperand(names, name);
                    ReserveNameDecoding(name);
                }

                void ReserveNameDecoding(ShellValue name)
                {
                    if (System.Text.Unicode.Utf8.IsValid(name.Bytes.Span))
                        Memory.Charge(PreparedQueryDecodeBytes(name));
                }
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

        return Executable(RemainingWords(commandValue));

        PreparedCommand.Executable Executable(CommandWords words) => new(
            words,
            commandName,
            ResolveCommandName(commandValue, commandName, lookupMode),
            executionOptions,
            completion,
            RedirectionLifetime.Command);

        PreparedCommand.Terminal InvalidOption(BuiltinKind kind, byte option) =>
            OptionFailure(kind, ShellValue.FromBytes(new[] { (byte)'-', option }), ": invalid option");

        PreparedCommand.Terminal OptionFailure(BuiltinKind kind, ShellValue option, string reason)
        {
            var usage = ShellValue.FromText(BuiltinDocumentation.For(kind).Synopsis(commandName));
            var newline = ShellValue.FromText("\n");
            var output = BuildTerminalMessage([commandValue, ShellValue.FromText(": "), option, ShellValue.FromText(reason),
                newline, commandValue, ShellValue.FromText(": usage: "), usage, newline]);
            return Terminal(2, output, CommandMessageChannel.StandardError, kind);
        }

        PreparedCommand.Terminal Help(BuiltinKind kind)
        {
            // Describe the hosted subset; do not promise Bash process replacement or execfail support.
            var documentation = BuiltinDocumentation.For(kind);
            var message = ShellValue.FromText($": {documentation.Synopsis(commandName)}\n    {documentation.Description}\n");
            return Terminal(2, BuildTerminalMessage([commandValue, message]), CommandMessageChannel.StandardOutput, kind);
        }

        ShellValue BuildTerminalMessage(ReadOnlySpan<ShellValue> parts)
        {
            long length = 0;
            foreach (var part in parts)
                length += part.Bytes.Length;
            if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();

            // The prepared command keeps the record through later expansion, redirections and host writes.
            Memory.Charge(length);
            var output = new byte[(int)length];
            Span<byte> remaining = output;
            foreach (var part in parts)
            {
                part.Bytes.Span.CopyTo(remaining);
                remaining = remaining[part.Bytes.Length..];
            }
            return ShellValue.FromBytes(output);
        }

        PreparedCommand.Terminal Terminal(int exitCode, ShellValue message, CommandMessageChannel channel, BuiltinKind kind) =>
            new(EvaluationResult.FromExitCode(exitCode), message, channel, RemainingWords(null), completion,
                // Direct exec retains redirections even on option failure/help; builtin exec restores them.
                kind == BuiltinKind.Exec && allowsPersistentRedirections
                    ? RedirectionLifetime.Evaluation : RedirectionLifetime.Command);

        async Task<ShellValue?> ReadFieldAsync()
        {
            while (!_state.HasPendingExpansionError)
            {
                if (fieldIndex < fields.Count)
                    return fields[fieldIndex++];
                if (wordIndex >= sourceWords.Count)
                    break;
                fields.Clear();
                fieldIndex = 0;
                var word = sourceWords[wordIndex++];
                if (word.Parts.Count == 1 && word.Parts[0].Kind == WordPartKind.SingleQuoted)
                    return word.Parts[0].GetUnexpandedValue();
                await AppendOperandValuesAsync(word, scope, fields, fieldMemory).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    fields.Clear();
            }
            return null;
        }

        CommandWords RemainingWords(ShellValue? first)
        {
            var count = checked(fields.Count - fieldIndex + (first.HasValue ? 1 : 0));
            if (count == 0)
                return new CommandWords(sourceWords, wordIndex, []);

            Memory.Charge(MemoryCapacity.ArrayBackingBytes<Word>(count));
            var captures = new Word[count];
            var captureIndex = 0;
            if (first is { } value)
                captures[captureIndex++] = CaptureLiteralWord(value);
            for (var index = fieldIndex; index < fields.Count; index++)
                captures[captureIndex++] = CaptureLiteralWord(fields[index]);
            return new CommandWords(sourceWords, wordIndex, captures);
        }
    }

}
