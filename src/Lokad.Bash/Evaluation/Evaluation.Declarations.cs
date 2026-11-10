using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Prepares declaration fields once; compound writes precede dispatch and later operand expansion.</summary>
    private async Task<PreparedDeclaration> PrepareDeclarationAsync(SimpleCommand command, ScopedEvaluation scope)
    {
        var commandName = command.Words[0].Parts[0].GetUtf8Text();
        if (!BuiltinClassification.TryResolve(commandName.AsSpan(), out var builtin))
            throw new InvalidOperationException("A declaration requires a declaration command name.");

        var options = new DeclarationOptions(builtin);
        var words = new List<Word>(command.Words.Count) { command.Words[0] };
        var operands = new List<PreparedDeclarationOperand>();
        for (var index = 1; index < command.Words.Count; index++)
        {
            var word = command.Words[index];
            if (command.DeclarationAssignments.TryGetValue(word, out var assignment))
            {
                options.EndOptions();
                if (assignment.ArrayValues is { } items)
                {
                    var bracket = assignment.Target.AsSpan().IndexOf((byte)'[');
                    var declaredSource = assignment.Target.Slice(0, bracket < 0 ? assignment.Target.Length : bracket);
                    if (!TryParseDeclarationReference(declaredSource, out var declaredReference))
                        throw new InvalidOperationException("A compound declaration requires a variable name.");
                    var declaredName = declaredReference.Name;
                    var declaredValue = declaredSource.ToValue();
                    var local = options.IsLocal(scope);
                    var existingLocal = scope.Context.FunctionContext?.Locals.Declares(declaredName) == true;
                    var resolved = SelectTarget(out var name, out var targetVariables, out var targetLocal);
                    var kind = SelectKind();
                    var context = options.Global && kind == ArrayKind.Indexed && targetVariables.AssociativeArrays.ContainsKey(name)
                        ? AssignmentContext.QuotedIndexedDeclaration : AssignmentContext.Declaration;
                    var expanded = await AwaitWordExpansionAsync(ExpandCompoundArrayItemsAsync(name, items, kind, assignment.Operator, context, scope))
                        .ConfigureAwait(false);
                    if (expanded == null)
                        break;

                    // RHS defaults can initialize a nameref. Bind after expansion without replaying its effects.
                    resolved = SelectTarget(out name, out targetVariables, out targetLocal);
                    var expansionKind = kind;
                    kind = SelectKind();
                    if (expansionKind != kind && kind == ArrayKind.Associative)
                        expanded = await ReclassifyAssociativeItemsAsync().ConfigureAwait(false);

                    if (!resolved)
                    {
                        // Compound RHS expansion precedes both lookup warnings and fallback creation.
                        await ReportCircularNameReferenceAsync(declaredName).ConfigureAwait(false);
                        if (local)
                            await ReportCircularNameReferenceAsync(declaredName).ConfigureAwait(false); // Local binding creation.
                        else if (options.ArrayKind == ArrayAssignmentKind.Auto)
                        {
                            // Implicit array kind first attempts ordinary binding, then array lookup.
                            await ReportCircularNameReferenceAsync(declaredName).ConfigureAwait(false);
                            await ReportCircularNameReferenceAsync(declaredName).ConfigureAwait(false);
                        }
                        if (!local || existingLocal)
                            targetVariables.RecreateDeclarationArray(name, kind);
                    }

                    // Declaration operands expand before readonly/type rejection, unlike direct assignments.
                    var memberAssignment = bracket >= 0 || !name.IsIdentifier
                        || targetVariables.TryResolveBinding(declaredName, selectOwner: null, out var selected)
                            && selected.Reference.Kind != VariableReferenceKind.Name;
                    if (!memberAssignment && !local && options.ArrayKind != ArrayAssignmentKind.Auto
                        && targetVariables.TryGetNameref(name, out _))
                        targetVariables.RecreateDeclarationArray(name, kind);
                    if (memberAssignment || (!local || targetLocal) && targetVariables.IsReadonly(name))
                    {
                        await ReportFatalExpansionAsync(bracket >= 0 ? assignment.Target.ToValue() : name.ToValue(), ShellValue.FromText(memberAssignment ? "cannot assign list to array member" : "readonly variable"), ExpansionFailureKind.AssignmentTarget)
                            .ConfigureAwait(false);
                        break;
                    }
                    var declarationBinding = options.Global
                        && scope.Context.TryResolveGlobalBinding(declaredName, scope.Variables, out var globalBinding)
                        ? globalBinding : new VariableBinding(VariableReference.ForName(name), targetVariables);
                    if (!local && (!options.Global || options.ArrayKind != ArrayAssignmentKind.Auto)
                        && HasConflictingArrayKind(declarationBinding.Owner, declarationBinding.Name, kind))
                    {
                        if (options.Global && scope.Context.FunctionContext != null)
                        {
                            await ReportExpansionDiagnosticAsync(declaredName.ToValue(), ShellValue.FromText(ArrayConversionMessage(kind))).ConfigureAwait(false);
                            AddField(declaredValue);
                            operands.Add(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: false));
                            continue;
                        }
                        await ReportFatalExpansionAsync((options.Global ? declaredName : name).ToValue(), ShellValue.FromText(ArrayConversionMessage(kind)), ExpansionFailureKind.AssignmentTarget).ConfigureAwait(false);
                        break;
                    }
                    var unsetReference = targetVariables.TryGetNameref(name, out var referenceTarget) && referenceTarget.IsEmpty;
                    if (local && scope.Context.FunctionContext is { } function)
                    {
                        // Redeclaring a local nameref localizes its referent, even when the reference already exists.
                        // An unset reference instead becomes the array, including implicit compound declarations.
                        var declarationKind = existingLocal && unsetReference
                            ? kind == ArrayKind.Associative ? ArrayAssignmentKind.Associative : ArrayAssignmentKind.Indexed
                            : options.ArrayKind;
                        if (!function.Locals.TryDeclare(scope.Variables, scope.Variables,
                                new VariableOperand(VariableOperandKind.Name, declaredReference, ShellValue.Empty), prefixes: null,
                                declarationKind, isNameref: false, ExpansionBudget.Words, ExpansionBudget.Memory))
                        {
                            var message = targetVariables.IsReadonly(name) ? "readonly variable" : ArrayConversionMessage(kind);
                            await ReportExpansionDiagnosticAsync(name.ToValue(), ShellValue.FromText(message)).ConfigureAwait(false);
                            // Dispatch still visits this name and later operands; a shadowing function can also run.
                            AddField(declaredValue);
                            operands.Add(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: false));
                            continue;
                        }
                    }
                    else if (unsetReference)
                    {
                        if (options.ArrayKind == ArrayAssignmentKind.Auto)
                            await ReportRemovingNamerefAsync(name).ConfigureAwait(false);
                        targetVariables.TryRemoveNamerefAttribute(name);
                    }
                    if (!await AwaitWordExpansionAsync(ApplyCompoundArrayItemsAsync(
                            name, assignment.Operator, kind, expanded, scope, targetVariables, declaredName)).ConfigureAwait(false))
                    {
                        if (!HasPendingExpansionError)
                            SetFatalExpansionError(ExpansionFailureKind.AssignmentTarget);
                        break;
                    }

                    AddField(declaredValue);
                    operands.Add(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: true));

                    bool SelectTarget(out BindingName targetName, out Variables variables, out bool isLocal)
                    {
                        // -g selects the declaration's initial binding globally. Compound writes
                        // through a nameref then follow its referent in the active scope.
                        var found = options.Global
                            ? scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables)
                                .TryResolveBinding(declaredName, _ => scope.Variables, out var binding)
                            : scope.Variables.TryResolveBinding(declaredName, selectOwner: null, out binding);
                        if (found && !local && options.ArrayKind != ArrayAssignmentKind.Auto)
                            binding = (options.Global ? scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables) : scope.Variables)
                                .SelectArrayDeclarationBinding(declaredName, binding);
                        targetName = !found || local && !existingLocal ? declaredName : binding.Name;
                        variables = !options.Global ? scope.Variables : found ? binding.Owner
                            : scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables);
                        isLocal = local && scope.Context.FunctionContext?.Locals.Declares(targetName) == true;
                        return found;
                    }

                    ArrayKind SelectKind() => options.ArrayKind switch
                    {
                        ArrayAssignmentKind.Associative => ArrayKind.Associative,
                        ArrayAssignmentKind.Indexed => ArrayKind.Indexed,
                        // In a function, -g checks the declared name's own array attribute before following a nameref.
                        _ when options.Global && scope.Context.FunctionContext != null => scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables)
                            .AssociativeArrays.ContainsKey(declaredName) ? ArrayKind.Associative : ArrayKind.Indexed,
                        _ => (!local || targetLocal) && targetVariables.AssociativeArrays.ContainsKey(name)
                            ? ArrayKind.Associative : ArrayKind.Indexed,
                    };

                    async Task<IReadOnlyList<ExpandedArrayItem>> ReclassifyAssociativeItemsAsync()
                    {
                        ExpansionBudget.Words.Charge(expanded.Count, 0);
                        var result = new List<ExpandedArrayItem>();
                        if (items.Count > 0 && items[0].Element != null)
                        {
                            foreach (var item in expanded)
                            {
                                if (item.Target != null) result.Add(item);
                                else await ReportExpansionDiagnosticAsync(name.ToValue(), ShellValue.FromText("must use subscript when assigning associative array")).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            for (var itemIndex = 0; itemIndex < expanded.Count; itemIndex += 2)
                            {
                                var key = Flatten(expanded[itemIndex]);
                                var value = itemIndex + 1 < expanded.Count ? Flatten(expanded[itemIndex + 1]) : ShellValue.Empty;
                                result.Add(new ExpandedArrayItem(VariableReference.ForElement(name, ShellSource.FromBytes(key.Bytes)),
                                    key, value, AssignmentOperator.Replace, PrefixWithElementZero: false));
                            }
                        }
                        return result;

                        ShellValue Flatten(ExpandedArrayItem item) => item.Target == null ? item.Value
                            : FlattenCompoundArrayItem(item.ExpandedSubscript, item.Value, item.Operator);
                    }
                }
                else
                {
                    // Shell target expansion precedes the RHS; builtin subscript interpretation happens on application.
                    var name = assignment.Target.AsSpan().IndexOf((byte)'[') >= 0
                        ? await AwaitWordExpansionAsync(WordExpansion.ExpandAssignmentValueAsync(
                            Tokenizer.ReadExpansionWord(assignment.Target, ExpansionWordSyntax.Parameter, ShellQuoteKind.None), scope))
                            .ConfigureAwait(false)
                        : assignment.Target.ToValue();
                    if (HasPendingExpansionError)
                        break;
                    var value = await AwaitWordExpansionAsync(WordExpansion.ExpandAssignmentValueAsync(assignment.Value, scope))
                        .ConfigureAwait(false);
                    if (HasPendingExpansionError)
                        break;
                    // Retain the pieces; ordinary dispatch flattens them through the shared word budget.
                    var operand = new PreparedDeclarationOperand(name, value, assignment.Operator, CompoundApplied: false);
                    words.Add(operand.ToWord());
                    operands.Add(operand);
                }
                continue;
            }

            var fields = await ExpandToWordsAsync(word, scope).ConfigureAwait(false);
            if (HasPendingExpansionError)
                break;
            foreach (var field in fields)
            {
                AddField(field);
                if (options.TryConsume(field, ExpansionBudget.Words))
                    continue;
                operands.Add(PrepareVariableOperand(field));
            }
        }

        return new PreparedDeclaration(command with { Words = words }, options, operands);

        static PreparedDeclarationOperand PrepareVariableOperand(ShellValue input) =>
            AssignmentWord.TrySplitOperand(ShellSource.FromBytes(input.Bytes), out var name, out var value, out var operation)
                ? new PreparedDeclarationOperand(name.ToValue(), value.ToValue(), operation, CompoundApplied: false)
                : new PreparedDeclarationOperand(input, null, AssignmentOperator.Replace, CompoundApplied: false);

        void AddField(ShellValue field) => words.Add(new Word([WordPart.FromValue(WordPartKind.SingleQuoted, field)]));

        static bool HasConflictingArrayKind(Variables variables, BindingName name, ArrayKind kind) =>
            kind == ArrayKind.Associative ? variables.IndexedArrays.ContainsKey(name) : variables.AssociativeArrays.ContainsKey(name);
    }

    private bool TryParseDeclarationReference(ShellSource source, out VariableReference reference)
        => VariableReference.TryParse(source, ExpansionBudget.Words, out reference);

    internal Task ReportCircularNameReferenceAsync(BindingName name) =>
        ReportExpansionDiagnosticAsync(ShellValue.FromText("warning"), ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append(name.ToValue().Bytes).Append(": circular name reference"u8).ToMemory()));

    internal Task ReportRemovingNamerefAsync(BindingName name) =>
        ReportExpansionDiagnosticAsync(ShellValue.FromText("warning"), ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append(name.ToValue().Bytes).Append(": removing nameref attribute"u8).ToMemory()));

    private static string ArrayConversionMessage(ArrayKind kind) => kind == ArrayKind.Associative
        ? "cannot convert indexed to associative array" : "cannot convert associative to indexed array";

    /// <summary>Null Value means a name without an explicit value, not an assigned empty value.</summary>
    private sealed record PreparedDeclarationOperand(ShellValue Name, ShellValue? Value, AssignmentOperator Operator, bool CompoundApplied)
    {
        // Frozen pieces reuse ordinary word flattening only when dispatch or inspection needs a joined value.
        public Word ToWord() => new(Value is { } value
            ? [WordPart.FromValue(WordPartKind.SingleQuoted, Name),
                WordPart.FromValue(WordPartKind.SingleQuoted, ShellValue.FromText(Operator == AssignmentOperator.Append ? "+=" : "=")),
                WordPart.FromValue(WordPartKind.SingleQuoted, value)]
            : [WordPart.FromValue(WordPartKind.SingleQuoted, Name)]);
    }

    private sealed record PreparedDeclaration(
        SimpleCommand Command, DeclarationOptions Options, IReadOnlyList<PreparedDeclarationOperand> Operands);

    private enum DeclarationAttributeChange { Unchanged, Set, Clear }

    /// <summary>Positive options select listing attributes even when a later option clears mutation intent.</summary>
    [Flags]
    private enum DeclarationListingAttributes { None = 0, Indexed = 1, Associative = 2, Nameref = 4, Exported = 8, Readonly = 16 }

    /// <summary>Consumes only the initial option fields; later fields retain their operand meaning.</summary>
    private sealed class DeclarationOptions(BuiltinKind builtin)
    {
        private bool _readingOptions = true;
        public BuiltinKind Builtin { get; } = builtin;
        public DeclarationAttributeChange IndexedChange { get; private set; }
        public DeclarationAttributeChange AssociativeChange { get; private set; }
        public ArrayAssignmentKind ArrayKind => AssociativeChange == DeclarationAttributeChange.Set
            ? ArrayAssignmentKind.Associative : IndexedChange == DeclarationAttributeChange.Set
                ? ArrayAssignmentKind.Indexed : ArrayAssignmentKind.Auto;
        public DeclarationAttributeChange NamerefChange { get; private set; }
        public DeclarationAttributeChange ExportChange { get; private set; } = builtin == BuiltinKind.Export
            ? DeclarationAttributeChange.Set : DeclarationAttributeChange.Unchanged;
        public DeclarationAttributeChange ReadonlyChange { get; private set; } = builtin == BuiltinKind.Readonly
            ? DeclarationAttributeChange.Set : DeclarationAttributeChange.Unchanged;
        public bool Nameref => NamerefChange == DeclarationAttributeChange.Set && ArrayKind == ArrayAssignmentKind.Auto;
        public bool Readonly => ReadonlyChange == DeclarationAttributeChange.Set;
        public bool Global { get; private set; }
        public bool Print { get; private set; }
        public bool Functions { get; private set; }
        public bool FunctionNamesOnly { get; private set; }
        public bool RemoveFunctions { get; private set; }
        public string InvalidFunctionAttribute { get; private set; } = string.Empty;
        public DeclarationListingAttributes ListingAttributes { get; private set; } = builtin switch
        {
            BuiltinKind.Export => DeclarationListingAttributes.Exported,
            BuiltinKind.Readonly => DeclarationListingAttributes.Readonly,
            _ => DeclarationListingAttributes.None
        };
        public ShellValue InvalidOption { get; private set; } = ShellValue.Empty;

        public bool IsLocal(ScopedEvaluation scope) => scope.Context.FunctionContext != null
            && (Builtin == BuiltinKind.Local || Builtin == BuiltinKind.Declare && !Global);

        public void EndOptions() => _readingOptions = false;

        public bool TryConsume(ShellValue field, AllocationAllowance allowance)
        {
            if (!_readingOptions)
                return false;
            var bytes = field.Bytes.Span;
            if (bytes.SequenceEqual("--"u8))
            {
                EndOptions();
                return true;
            }
            var remove = bytes.Length > 0 && bytes[0] == (byte)'+' && Builtin is BuiltinKind.Declare or BuiltinKind.Local;
            if (bytes.Length < 2 || bytes[0] != (byte)'-' && !remove)
            {
                EndOptions();
                return false;
            }

            // Reuse grouped-option parsing; only declaration builtins give '+' an option meaning.
            var optionField = field;
            if (remove)
            {
                allowance.Charge(0, bytes.Length);
                var normalized = bytes.ToArray();
                normalized[0] = (byte)'-';
                optionField = ShellValue.FromBytes(normalized);
            }
            var parser = new CommandLineOptionParser([optionField], allowSingleDashLongOptions: false);
            while (parser.TryRead(out var item))
            {
                if (item.Kind != CommandLineOptionKind.ShortOption)
                {
                    if (InvalidOption.IsEmpty) InvalidOption = field;
                    continue;
                }
                if (!remove && InvalidFunctionAttribute.Length == 0
                    && (item.ShortName is (byte)'a' or (byte)'A' || item.ShortName == (byte)'n' && Builtin != BuiltinKind.Export))
                    InvalidFunctionAttribute = "-" + (char)item.ShortName;
                switch (item.ShortName)
                {
                    case (byte)'a': IndexedChange = ReadChange(IndexedChange, DeclarationListingAttributes.Indexed); break;
                    case (byte)'A': AssociativeChange = ReadChange(AssociativeChange, DeclarationListingAttributes.Associative); break;
                    case (byte)'n' when Builtin == BuiltinKind.Export: ExportChange = DeclarationAttributeChange.Clear; break;
                    case (byte)'n' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: NamerefChange = ReadChange(NamerefChange, DeclarationListingAttributes.Nameref); break;
                    case (byte)'r' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: ReadonlyChange = ReadChange(ReadonlyChange, DeclarationListingAttributes.Readonly); break;
                    case (byte)'x' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: ExportChange = ReadChange(ExportChange, DeclarationListingAttributes.Exported); break;
                    case (byte)'g' when Builtin == BuiltinKind.Declare: Global |= !remove; break;
                    case (byte)'p': Print = true; break;
                    case (byte)'f':
                    case (byte)'F' when Builtin is BuiltinKind.Declare or BuiltinKind.Local:
                        FunctionNamesOnly |= item.ShortName == (byte)'F';
                        Functions |= !remove;
                        RemoveFunctions |= remove;
                        break;
                    default:
                        if (InvalidOption.IsEmpty) InvalidOption = ShellValue.FromBytes(new byte[] { bytes[0], (byte)item.ShortName });
                        break;
                }
            }
            return true;

            // Bash clears a requested attribute even when the same command also requests setting it.
            DeclarationAttributeChange ReadChange(DeclarationAttributeChange current, DeclarationListingAttributes attribute)
            {
                if (!remove) ListingAttributes |= attribute;
                return remove || current == DeclarationAttributeChange.Clear
                    ? DeclarationAttributeChange.Clear : DeclarationAttributeChange.Set;
            }
        }
    }
}
