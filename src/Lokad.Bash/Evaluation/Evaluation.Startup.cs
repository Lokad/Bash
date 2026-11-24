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
        ReplacePositionalParameters(positionalParameters);
    }

    private (string CommandName, IReadOnlyList<ExportedFunction> Functions)? _inheritedFunctions;

    internal void SetInheritedFunctions(string commandName, IReadOnlyList<ExportedFunction> functions)
    {
        if (functions.Count != 0)
            _inheritedFunctions = (commandName, functions);
    }

    private async Task<bool> TryImportInheritedFunctionsAsync(ScopedEvaluation scope)
    {
        if (_inheritedFunctions is not { } inherited)
            return true;
        _inheritedFunctions = null;

        using var command = Memory.BeginCommand(scope);
        var bindings = new List<FunctionBinding>();
        try
        {
            foreach (var exported in inherited.Functions)
            {
                CheckCancellation();
                var owner = new SourceMemory(Memory.Budget.CreateScope());
                try
                {
                    var source = exported.Source;
                    owner.SyntaxMemory.Grow(32L + source.Length);
                    source = source.ToArray();
                    var parser = new ShellScanContext { SyntaxMemory = owner.SyntaxMemory };
                    if (!Parser.TryParseScript(source, parser, out var parsed, out var failure))
                    {
                        await ReportSyntaxErrorAsync(failure.Error).ConfigureAwait(false);
                        return false;
                    }
                    if (parsed.Commands is not [FunctionDefinition definition] || !definition.Name.CanExport)
                    {
                        await ReportSyntaxErrorAsync(new SyntaxError("Expected a single exported function definition.", 0))
                            .ConfigureAwait(false);
                        return false;
                    }
                    owner.SyntaxMemory.ReleaseUnusedCapacityOnPublication();
                    if (parser.RejectedCaptureReads != 0 && !await ValidateImportSpellingAsync(definition).ConfigureAwait(false))
                        return false;
                    if (_state.Functions.ContainsKey(definition.Name.Value))
                        continue;
                    Memory.PrepareListCapacity(bindings, 1);
                    Memory.Charge(256);
                    var binding = new FunctionBinding(definition, FunctionAttributes.Exported, owner, Memory);
                    bindings.Add(binding);
                    binding.Retain();
                }
                finally { owner.Release(); }
            }

            foreach (var binding in bindings)
                _state.SetFunctionBinding(binding);
            return true;
        }
        finally
        {
            foreach (var binding in bindings)
                binding.Release();
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
                await ReportUnexpectedSyntaxAsync(token, inherited.CommandName, definition.Name.Value,
                    SyntaxDiagnosticOrigin.Script, line, includeLine: true).ConfigureAwait(false);
            }
            else
                await ReportSyntaxErrorAsync(failed.Error).ConfigureAwait(false);
            return false;
        }
    }
}
