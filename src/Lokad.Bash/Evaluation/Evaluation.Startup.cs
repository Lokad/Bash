using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Initializes evaluation from captured interpreter values, independently of command hosting.</summary>
    /// <remarks>
    /// Captured descriptor bindings are forked and their handles remain borrowed. Environment and positional
    /// entries initialize caller-prepared state before the engine installs its runtime budget.
    /// </remarks>
    internal Evaluation(
        ParsedScript script,
        EvaluationOptions options,
        EvaluationDescriptorSetup descriptors,
        IReadOnlyList<EnvironmentVariable> environment,
        ShellValue argumentZero,
        IReadOnlyList<ShellValue> positionalParameters)
        : this(script, descriptors, publishNextStep: null, onComplete: null,
            options, new MemoryGovernor(long.MaxValue), sourceMemory: null)
    {
        foreach (var variable in environment)
            SetExportedVariable(variable.Name, variable.Value);
        _vars.ArgumentZero = argumentZero;
        _vars.SubshellLevel = 0;
        ReplacePositionalParameters(positionalParameters);
    }

    private (string CommandName, IReadOnlyList<ExportedFunction> Functions,
        BashCommandInheritance.ImportEntry[]? ImportOrder)? _inheritedFunctions;
    private (ShellValue Name, SourceMemory.ReaderKind Kind)? _inheritedReader;

    internal void SetInheritedReader(ShellValue name, SourceMemory.ReaderKind kind)
    {
        _inheritedReader = (name, kind);
        _state.UsesCommandStringExpansionExit = kind == SourceMemory.ReaderKind.CommandString;
    }

    internal void SetInheritedFunctions(string commandName, IReadOnlyList<ExportedFunction> functions) =>
        SetInheritedFunctions(commandName, functions, null);

    internal void SetInheritedFunctions(string commandName, IReadOnlyList<ExportedFunction> functions,
        BashCommandInheritance.FunctionImports? imports)
    {
        if (functions.Count != 0)
            _inheritedFunctions = (commandName, functions,
                imports != null && ReferenceEquals(imports.Functions, functions) ? imports.Entries : null);
    }

    private async Task<(bool Ready, ShellValue? DiagnosticName)> TryImportInheritedFunctionsAsync(ScopedEvaluation scope)
    {
        if (_inheritedFunctions is not { } inherited)
            return (true, null);
        _inheritedFunctions = null;

        using var command = Memory.BeginCommand(scope);
        var bindings = new List<EvaluationState.FunctionImport>();
        var skippedExport = false;
        try
        {
            for (var ordinal = 0; ordinal < inherited.Functions.Count; ordinal++)
            {
                CheckCancellation();
                var entry = inherited.ImportOrder?[ordinal] ?? new BashCommandInheritance.ImportEntry(ordinal, null);
                var owner = new SourceMemory(Memory.Budget.CreateScope(), parent: null, SourceMemory.ReaderKind.ImportedFunction);
                try
                {
                    var source = inherited.Functions[entry.Index].Source;
                    owner.SyntaxMemory.Grow(32L + source.Length);
                    source = source.ToArray();
                    var parser = new ShellScanContext
                    {
                        SyntaxMemory = owner.SyntaxMemory,
                        RetainCaptureEofDelimiter = entry.DamagedName != null
                    };
                    if (!Parser.TryParseScript(source, parser, out var parsed, out var failure))
                    {
                        if (entry.DamagedName is { } name && failure is { Kind: not ParserFailureKind.Limit, UnclosedDelimiter: { } delimiter })
                        {
                            await ReportTruncatedExportAsync(name.Value, delimiter, source).ConfigureAwait(false);
                            skippedExport = true;
                            continue;
                        }
                        await ReportSyntaxErrorAsync(failure.Error).ConfigureAwait(false);
                        return (false, null);
                    }
                    if (parsed.Commands is not [FunctionDefinition definition] || !definition.Name.CanExport)
                    {
                        await ReportSyntaxErrorAsync(new SyntaxError("Expected a single exported function definition.", 0))
                            .ConfigureAwait(false);
                        return (false, null);
                    }
                    owner.SyntaxMemory.ReleaseUnusedCapacityOnPublication();
                    if (parser.RejectedCaptureReads != 0 && !await ValidateImportSpellingAsync(definition).ConfigureAwait(false))
                        return (false, null);
                    if (_state.TryGetFunction(definition.Name.Value, out _))
                        continue;
                    Memory.PrepareListCapacity(bindings, 1);
                    Memory.Charge(EvaluationState.FunctionPublicationBytes);
                    if (parsed.Locations is { } locations)
                        FunctionDefinitionFormatter.ProjectImportLocations(definition, locations, owner.Budget, _cancellationToken);
                    var binding = new FunctionBinding(definition, FunctionAttributes.Exported, owner, Memory)
                    {
                        // The serialized function opens on the import reader's line zero.
                        Coordinates = new CommandCoordinates(parsed.Locations, -1, 0, 0) { CompoundOpenLine = 0 }
                    };
                    bindings.Add(new EvaluationState.FunctionImport(binding, entry.Index, 0));
                    binding.Retain();
                }
                finally { owner.Release(); }
            }

            _state.PublishFunctionImports(bindings, inherited.ImportOrder is { Length: > 1 });
            return (true, skippedExport ? _vars.ArgumentZero : null);
        }
        finally
        {
            foreach (var import in bindings)
                import.Binding.Release();
        }

        async Task ReportTruncatedExportAsync(ShellValue name, UnclosedDelimiter delimiter, ReadOnlyMemory<byte> source)
        {
            var descriptor = GetStdErrDescriptor();
            if (descriptor.IsClosed)
                return;
            // The function import reader begins on line zero.
            var line = source.Span[..delimiter.ReadPosition].Count((byte)'\n') + delimiter.LineAdvance;
            var invocationName = ShellValue.FromText(inherited.CommandName);
            await ReportUnclosedDelimiterAsync(delimiter, line, invocationName, name,
                SyntaxDiagnosticOrigin.Script, SyntaxDiagnosticDetail.Complete).ConfigureAwait(false);
            await WriteDiagnosticAsync(descriptor, [invocationName],
                [ShellValue.FromText("error importing function definition for `"), name, ShellValue.FromText("'")], scope: null)
                .ConfigureAwait(false);
        }

        async Task<bool> ValidateImportSpellingAsync(FunctionDefinition definition)
        {
            using var validation = Memory.Budget.CreateScope();
            using var source = new BoundedByteMemoryBuilder(0, Parser.MaximumScriptBytes, validation);
            if (!FunctionDefinitionFormatter.TryAppendImportValidation(definition, source, _cancellationToken))
                throw new ByteMemoryBuilder.LimitExceededException();
            using var syntax = new MemoryCapacity(validation, 0);
            var unit = Parser.TryParseCommandUnits(source.ToMemory(), new ShellScanContext { SyntaxMemory = syntax }).First();
            if (unit is not CommandUnitParseResult.Failed failed)
                return true;
            if (failed.Failure.UnexpectedToken is { } token)
            {
                // Bash imports before setting the script reader's initial line to one.
                var line = source.ToMemory().Span[..token.ReadPosition].Count((byte)'\n') + token.LineAdvance;
                await ReportUnexpectedSyntaxAsync(token, ShellValue.FromText(inherited.CommandName), definition.Name.Value,
                    SyntaxDiagnosticOrigin.Script, line, SyntaxDiagnosticDetail.Complete).ConfigureAwait(false);
            }
            else
                await ReportSyntaxErrorAsync(failed.Error).ConfigureAwait(false);
            return false;
        }
    }
}
