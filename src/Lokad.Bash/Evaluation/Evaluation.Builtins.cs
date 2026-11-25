using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    private enum ShortOptionField { Operand, Terminator, Help, Flags }

    private static ShortOptionField ClassifyShortOptionField(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != (byte)'-')
            return ShortOptionField.Operand;
        if (bytes[1] != (byte)'-')
            return ShortOptionField.Flags;
        if (bytes.Length == 2)
            return ShortOptionField.Terminator;
        return bytes.SequenceEqual("--help"u8) ? ShortOptionField.Help : ShortOptionField.Flags;
    }

    /// <summary>Scans leading short options and borrows their bytes from the expanded arguments.</summary>
    private struct ShortOptionCursor(IReadOnlyList<ShellValue> arguments)
    {
        private ReadOnlyMemory<byte> _group;
        private bool _finished;

        public int ArgumentIndex { get; private set; } = 1;
        public bool RequestedHelp { get; private set; }

        public bool TryReadFlag(out ReadOnlyMemory<byte> flag)
        {
            flag = default;
            if (_group.IsEmpty)
            {
                if (_finished || ArgumentIndex == arguments.Count)
                    return false;
                var bytes = arguments[ArgumentIndex].Bytes;
                var field = ClassifyShortOptionField(bytes.Span);
                if (field == ShortOptionField.Operand)
                {
                    _finished = true;
                    return false;
                }
                ArgumentIndex++;
                if (field is ShortOptionField.Help or ShortOptionField.Terminator)
                {
                    RequestedHelp = field == ShortOptionField.Help;
                    _finished = true;
                    return false;
                }
                _group = bytes[1..];
            }

            flag = _group[..1];
            _group = _group[1..];
            return true;
        }

        public bool TryReadOperand(out ShellValue operand)
        {
            if (!_group.IsEmpty)
            {
                operand = ShellValue.FromBytes(_group);
                _group = default;
                return true;
            }
            if (ArgumentIndex < arguments.Count)
            {
                operand = arguments[ArgumentIndex++];
                return true;
            }

            operand = default;
            return false;
        }
    }

    /// <summary>
    /// Resolves command-control builtins by rewriting their target into the normal command path.
    /// </summary>
    /// <remarks>
    /// Wrapper targets share ordinary command-field selection; lookup, completion and redirection lifetime stay separate.
    /// </remarks>
    private async Task<PreparedCommand> PrepareCommandAsync(
        CommandWords sourceWords,
        ShellValue commandValue,
        ScopedEvaluation scope)
    {
        var lookupMode = CommandLookupMode.Normal;
        ExecOptions? executionOptions = null;
        var completion = CommandCompletion.Continue;
        var allowsPersistentRedirections = true;
        // Keep source words and surplus expansion fields separate; wrappers consume them without rebuilding the suffix.
        var wordIndex = 1;
        var lastReadArgument = commandValue;
        var resolution = ResolveCommandName(commandValue, lookupMode, out var registeredName);
        var commandName = registeredName ?? await DecodeExpandedValueAsync(commandValue).ConfigureAwait(false);
        if (resolution is not CommandResolution.EvaluatorBuiltin
            { Kind: BuiltinKind.Builtin or BuiltinKind.Command or BuiltinKind.Exec })
            return Executable(sourceWords, resolution);

        using var fieldMemory = new MemoryCapacity(Memory.Budget, 0);
        var fields = new List<ShellValue>();
        var fieldIndex = 0;

        while (resolution is CommandResolution.EvaluatorBuiltin
               { Kind: BuiltinKind.Builtin or BuiltinKind.Command or BuiltinKind.Exec } wrapper)
        {
            var builtinKind = wrapper.Kind;

            var next = await ReadFieldAsync().ConfigureAwait(false);
            if (builtinKind == BuiltinKind.Builtin)
            {
                allowsPersistentRedirections = false;
                if (next is not { } first)
                    return Terminal(0, diagnostic: null, help: null, builtinKind);
                var field = ClassifyShortOptionField(first.Bytes.Span);
                if (field == ShortOptionField.Help)
                    return Help(builtinKind);
                if (field == ShortOptionField.Terminator)
                    next = await ReadFieldAsync().ConfigureAwait(false);
                else if (field == ShortOptionField.Flags)
                    return InvalidOption(builtinKind, first.Bytes.Span[1]);

                if (next is not { } name)
                    return Terminal(0, diagnostic: null, help: null, builtinKind);
                if (!BuiltinClassification.TryResolve(name.Bytes.Span, out var targetName, out var targetKind))
                {
                    return Terminal(1, new PreparedWrapperDiagnostic(commandValue, name, "not a shell builtin",
                        UsageKind: null, commandName), help: null, builtinKind);
                }

                resolution = CommandResolution.ForBuiltin(targetKind);
                commandName = targetName;
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
                var field = ClassifyShortOptionField(first.Bytes.Span);
                if (field == ShortOptionField.Help)
                    return Help(builtinKind);
                if (field == ShortOptionField.Terminator)
                {
                    next = await ReadFieldAsync().ConfigureAwait(false);
                    break;
                }

                if (field == ShortOptionField.Operand)
                    break;

                var flags = first.Bytes[1..];
                while (!flags.IsEmpty)
                {
                    var flag = flags.Span[0];
                    flags = flags[1..];
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
                            if (!flags.IsEmpty)
                            {
                                argumentZero = ShellValue.FromBytes(flags);
                                flags = default;
                                break;
                            }

                            argumentZero = await ReadFieldAsync().ConfigureAwait(false);
                            if (!argumentZero.HasValue)
                                return OptionFailure(builtinKind, ShellValue.FromText("-a"), "option requires an argument");
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
                    names.Count == 0 ? lastReadArgument : names[^1],
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
                return Terminal(0, diagnostic: null, help: null, builtinKind);

            commandValue = target;
            lookupMode = CommandLookupMode.BypassFunctions;
            resolution = ResolveCommandName(commandValue, lookupMode, out registeredName);
            commandName = registeredName ?? await DecodeExpandedValueAsync(commandValue).ConfigureAwait(false);
            if (builtinKind == BuiltinKind.Exec)
            {
                // A nested virtual exec selects a new name, but cannot restore already-cleared exports.
                executionOptions = new ExecOptions(
                    argumentZero, login, clearEnvironment || executionOptions?.ClearEnvironment == true);
                completion = CommandCompletion.ExitEvaluation;
            }
        }

        return Executable(RemainingWords(commandValue), resolution);

        PreparedCommand.Executable Executable(CommandWords words, CommandResolution selected) => new(
            words,
            commandValue,
            commandName,
            selected,
            executionOptions,
            completion,
            RedirectionLifetime.Command);

        PreparedCommand.Terminal InvalidOption(BuiltinKind kind, byte option) =>
            OptionFailure(kind, ShellValue.FromBytes(new[] { (byte)'-', option }), "invalid option");

        PreparedCommand.Terminal OptionFailure(BuiltinKind kind, ShellValue option, string reason) =>
            Terminal(2, new PreparedWrapperDiagnostic(commandValue, option, reason, kind, commandName), help: null, kind);

        PreparedCommand.Terminal Help(BuiltinKind kind) =>
            Terminal(2, diagnostic: null, new PreparedHelp(commandValue, commandName, kind), kind);

        PreparedCommand.Terminal Terminal(int exitCode, PreparedWrapperDiagnostic? diagnostic, PreparedHelp? help, BuiltinKind kind) =>
            new(EvaluationResult.FromExitCode(exitCode), diagnostic, help, RemainingWords(null), lastReadArgument, completion,
                // Direct exec retains redirections even on option failure/help; builtin exec restores them.
                kind == BuiltinKind.Exec && allowsPersistentRedirections
                    ? RedirectionLifetime.Evaluation : RedirectionLifetime.Command);

        async ValueTask<ShellValue?> ReadFieldAsync()
        {
            while (!_state.HasPendingExpansionError)
            {
                if (fieldIndex < fields.Count)
                    return lastReadArgument = fields[fieldIndex++];
                if (wordIndex >= sourceWords.Count)
                    break;
                fields.Clear();
                fieldIndex = 0;
                var word = sourceWords[wordIndex++];
                if (word.Parts.Count == 1 && word.Parts[0].Kind == WordPartKind.SingleQuoted)
                    return lastReadArgument = word.Parts[0].GetUnexpandedValue();
                await AppendOperandValuesAsync(word, scope, fields, fieldMemory).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    fields.Clear();
            }
            return null;
        }

        CommandWords RemainingWords(ShellValue? first)
        {
            if (!first.HasValue && fieldIndex == fields.Count)
                return sourceWords.Slice(wordIndex);
            // A target read from the captured prefix can stay at the head of that range.
            // Wrapper expansion only fills its own buffer after consuming the prefix.
            if (wordIndex <= sourceWords.CapturedCount)
                return sourceWords.Slice(wordIndex - 1);
            return sourceWords.WithCapturedPrefix(wordIndex, CaptureLiteralWords(fields, fieldIndex, first));
        }
    }

}
