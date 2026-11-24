using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using BindingAttributes = Lokad.Bash.Evaluation.Variables.BindingAttributes;
using CaseAttributeChange = Lokad.Bash.Evaluation.Variables.CaseAttributeChange;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Prepares declaration fields once; compound writes precede dispatch and later operand expansion.</summary>
    private async Task<PreparedDeclaration> PrepareDeclarationAsync(
        SimpleCommand command, CommandWords sourceWords, ScopedEvaluation scope, bool captureCommandWords)
    {
        var commandName = sourceWords[0].Parts[0].GetUtf8Text();
        if (!BuiltinClassification.TryResolve(commandName.AsSpan(), out var builtin))
            throw new InvalidOperationException("A declaration requires a declaration command name.");

        var options = new DeclarationOptions(builtin);
        // Parser-recognized assignments run before resolution. A shadowing function can still receive the words.
        var words = captureCommandWords ? new List<Word>() : null;
        if (words != null)
            AddOperand(words, sourceWords[0]);
        var operands = new List<PreparedDeclarationOperand>();
        var fields = new List<ShellValue>();
        ShellValue? lastArgument = null;
        PreparedDeclarationOperand? lastAssignment = null;
        var hasDeclarationAssignments = command.DeclarationAssignments.Count != 0;
        var readCompoundTransforms = true;
        var compoundGlobal = false;
        var compoundArrayKind = ArrayAssignmentKind.Auto;
        var compoundInteger = false;
        var compoundInherit = false;
        var compoundCase = BindingAttributes.None;
        byte compoundAttributeSign = 0;
        for (var index = 1; index < sourceWords.Count; index++)
        {
            var word = sourceWords[index];
            if (hasDeclarationAssignments)
                ReadCompoundOptions(word);
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
                    var local = options.IsLocal(scope, compoundGlobal);
                    var setCompoundTransforms = compoundAttributeSign != (byte)'+'
                        || compoundGlobal || compoundArrayKind != ArrayAssignmentKind.Auto;
                    var inherit = compoundInherit && setCompoundTransforms;
                    var existingLocal = scope.Context.FunctionContext?.Locals.Declares(declaredName) == true;
                    var resolved = SelectTarget(out var name, out var targetVariables, out var targetLocal);
                    var kind = SelectKind();
                    using var itemMemory = items.Count == 0 ? null : new MemoryCapacity(Memory.Budget, 0);
                    IReadOnlyList<ExpandedArrayItem> expanded = [];
                    if (itemMemory != null)
                    {
                        if (!await AwaitWordExpansionAsync(ValidateCompoundInitializerCapturesAsync(items, scope)).ConfigureAwait(false))
                            break;
                        var context = compoundGlobal && kind == ArrayKind.Indexed && targetVariables.AssociativeArrays.ContainsKey(name)
                            ? AssignmentContext.QuotedIndexedDeclaration : AssignmentContext.Declaration;
                        var expandedItems = await AwaitWordExpansionAsync(ExpandCompoundArrayItemsAsync(
                            name, items, kind, assignment.Operator, context, scope, itemMemory))
                            .ConfigureAwait(false);
                        if (expandedItems == null)
                            break;

                        // RHS defaults can initialize a nameref. Bind after expansion without replaying its effects.
                        resolved = SelectTarget(out name, out targetVariables, out targetLocal);
                        var expansionKind = kind;
                        kind = SelectKind();
                        if (expansionKind != kind && kind == ArrayKind.Associative)
                            await ReclassifyAssociativeItemsAsync(expandedItems).ConfigureAwait(false);
                        expanded = expandedItems;
                    }

                    if (!resolved)
                    {
                        // Compound RHS expansion precedes both lookup warnings and fallback creation.
                        await ReportCircularNameReferenceAsync(declaredName).ConfigureAwait(false);
                        if (local)
                            await ReportCircularNameReferenceAsync(declaredName).ConfigureAwait(false); // Local binding creation.
                        else if (compoundArrayKind == ArrayAssignmentKind.Auto)
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
                    if (!memberAssignment && !local && compoundArrayKind != ArrayAssignmentKind.Auto
                        && targetVariables.TryGetNameref(name, out _))
                        targetVariables.RecreateDeclarationArray(name, kind);
                    if (memberAssignment || (!local || targetLocal) && targetVariables.IsReadonly(name))
                    {
                        await ReportAssignmentTargetFailureAsync(assignment.Target.ToValue(),
                                memberAssignment ? "cannot assign list to array member" : "readonly variable", scope)
                            .ConfigureAwait(false);
                        break;
                    }
                    var declarationBinding = compoundGlobal
                        && scope.Context.TryResolveGlobalBinding(declaredName, scope.Variables, out var globalBinding)
                        ? globalBinding : new VariableBinding(VariableReference.ForName(name), targetVariables);
                    if (!local && (!compoundGlobal || compoundArrayKind != ArrayAssignmentKind.Auto)
                        && HasConflictingArrayKind(declarationBinding.Owner, declarationBinding.Name, kind))
                    {
                        if (compoundGlobal && scope.Context.FunctionContext != null)
                        {
                            await ReportExpansionDiagnosticAsync(declaredName.ToValue(), ShellValue.FromText(ArrayConversionMessage(kind))).ConfigureAwait(false);
                            AddAssignment(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: false));
                            continue;
                        }
                        await ReportFatalExpansionAsync((compoundGlobal ? declaredName : name).ToValue(), ShellValue.FromText(ArrayConversionMessage(kind)), ExpansionFailureKind.AssignmentTarget).ConfigureAwait(false);
                        break;
                    }
                    var unsetReference = targetVariables.TryGetNameref(name, out var referenceTarget) && referenceTarget.IsEmpty;
                    if (local && scope.Context.FunctionContext is { } function)
                    {
                        // Redeclaring a local nameref localizes its referent, even when the reference already exists.
                        // An unset reference instead becomes the array, including implicit compound declarations.
                        var declarationKind = existingLocal && unsetReference
                            ? kind == ArrayKind.Associative ? ArrayAssignmentKind.Associative : ArrayAssignmentKind.Indexed
                            : compoundArrayKind;
                        if (!function.Locals.TryDeclare(scope.Variables, scope.Variables,
                                new VariableOperand(VariableOperandKind.Name, declaredReference, ShellValue.Empty), prefixes: null,
                                declarationKind, isNameref: false, inherit, caseChange: default, Memory))
                        {
                            if (targetVariables.IsReadonly(name))
                                await ReportCommandDiagnosticAsync([function.FunctionName],
                                    [name.ToValue(), ShellValue.FromText(": readonly variable")], scope).ConfigureAwait(false);
                            else
                                await ReportExpansionDiagnosticAsync(name.ToValue(), ShellValue.FromText(ArrayConversionMessage(kind)))
                                    .ConfigureAwait(false);
                            // Dispatch still visits this name and later operands; a shadowing function can also run.
                            AddAssignment(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: false));
                            continue;
                        }
                    }
                    else if (unsetReference)
                    {
                        if (compoundArrayKind == ArrayAssignmentKind.Auto)
                            await ReportRemovingNamerefAsync(name).ConfigureAwait(false);
                        targetVariables.TryRemoveNamerefAttribute(name);
                    }
                    if (compoundInteger)
                        targetVariables.SetIntegerBinding(name, setCompoundTransforms);
                    targetVariables.SetCaseAttributes(name, GetCompoundCaseChange(setCompoundTransforms));
                    if (!await AwaitWordExpansionAsync(ApplyCompoundArrayItemsAsync(
                            name, assignment.Operator, kind, expanded, scope, targetVariables, declaredName,
                            initializeReadonly: false)).ConfigureAwait(false))
                    {
                        if (!HasPendingExpansionError)
                            SetFatalExpansionError(ExpansionFailureKind.AssignmentTarget);
                        break;
                    }

                    // Dispatch still sees a name. When expanded options select another scope or type,
                    // apply the ordinary named declaration there without repeating the initializer.
                    var applied = compoundGlobal == options.Global && compoundArrayKind == options.ArrayKind;
                    AddAssignment(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: applied));

                    bool SelectTarget(out BindingName targetName, out Variables variables, out bool isLocal)
                    {
                        // -g selects the declaration's initial binding globally. Compound writes
                        // through a nameref then follow its referent in the active scope.
                        var found = compoundGlobal
                            ? scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables)
                                .TryResolveBinding(declaredName, _ => scope.Variables, out var binding)
                            : scope.Variables.TryResolveBinding(declaredName, selectOwner: null, out binding);
                        if (found && !local && compoundArrayKind != ArrayAssignmentKind.Auto)
                            binding = (compoundGlobal ? scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables) : scope.Variables)
                                .SelectArrayDeclarationBinding(declaredName, binding);
                        targetName = !found || local && !existingLocal ? declaredName : binding.Name;
                        variables = !compoundGlobal ? scope.Variables : found ? binding.Owner
                            : scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables);
                        isLocal = local && scope.Context.FunctionContext?.Locals.Declares(targetName) == true;
                        return found;
                    }

                    ArrayKind SelectKind() => compoundArrayKind switch
                    {
                        ArrayAssignmentKind.Associative => ArrayKind.Associative,
                        ArrayAssignmentKind.Indexed => ArrayKind.Indexed,
                        // In a function, -g checks the declared name's own array attribute before following a nameref.
                        _ when compoundGlobal && scope.Context.FunctionContext != null => scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables)
                            .AssociativeArrays.ContainsKey(declaredName) ? ArrayKind.Associative : ArrayKind.Indexed,
                        _ => (!local || targetLocal || inherit) && targetVariables.AssociativeArrays.ContainsKey(name)
                            ? ArrayKind.Associative : ArrayKind.Indexed,
                    };

                    async Task ReclassifyAssociativeItemsAsync(List<ExpandedArrayItem> expandedItems)
                    {
                        // Write only into the consumed prefix. The unread items keep their original order.
                        var retained = 0;
                        if (items.Count > 0 && items[0].Element != null)
                        {
                            for (var itemIndex = 0; itemIndex < expandedItems.Count; itemIndex++)
                            {
                                CheckCancellation();
                                var item = expandedItems[itemIndex];
                                if (item.Subscript != null)
                                    expandedItems[retained++] = item;
                                else
                                    await ReportExpansionDiagnosticAsync(name.ToValue(), ShellValue.FromText("must use subscript when assigning associative array")).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            for (var itemIndex = 0; itemIndex < expandedItems.Count; itemIndex += 2)
                            {
                                CheckCancellation();
                                var key = Flatten(expandedItems[itemIndex]);
                                var value = itemIndex + 1 < expandedItems.Count ? Flatten(expandedItems[itemIndex + 1]) : ShellValue.Empty;
                                expandedItems[retained++] = new ExpandedArrayItem(ShellSource.FromBytes(key.Bytes),
                                    key, value, AssignmentOperator.Replace, PrefixWithElementZero: false) { IsAssociativePair = true };
                            }
                        }
                        expandedItems.RemoveRange(retained, expandedItems.Count - retained);

                        ShellValue Flatten(ExpandedArrayItem item) => item.Subscript == null ? item.Value
                            : FlattenCompoundArrayItem(item.ExpandedSubscript, item.Value, item.Operator);
                    }
                }
                else
                {
                    // Shell target expansion precedes the RHS; builtin subscript interpretation happens on application.
                    var name = assignment.Target.AsSpan().IndexOf((byte)'[') >= 0
                        ? await AwaitWordExpansionAsync(WordExpansion.ExpandSubscriptAsync(
                            assignment.Target, scope, ExpansionPurpose.Scalar, allowLeadingTilde: true))
                            .ConfigureAwait(false)
                        : assignment.Target.ToValue();
                    if (HasPendingExpansionError)
                        break;
                    var value = await AwaitWordExpansionAsync(WordExpansion.ExpandAssignmentValueAsync(assignment.Value,
                        scope with { SelectorParameterDiagnostics = false, ExpansionWord = word }))
                        .ConfigureAwait(false);
                    if (HasPendingExpansionError)
                        break;
                    AddAssignment(new PreparedDeclarationOperand(name, value, assignment.Operator, CompoundApplied: false));
                }
                continue;
            }

            if (scope.CanExpand && GetLiteralWithoutGlob(word) is { } literal)
            {
                CheckCancellation();
                AddField(literal.ToValue(Memory), word, remainingFields: 1);
                continue;
            }

            await AppendOperandValuesAsync(word, scope, fields).ConfigureAwait(false);
            if (HasPendingExpansionError)
                break;
            if (words != null)
                Memory.PrepareListCapacity(words, fields.Count);
            for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                AddField(fields[fieldIndex], sourceWord: null, remainingFields: fields.Count - fieldIndex);
            fields.Clear();
        }

        Memory.Workspace.Resize(Memory.Workspace.Bytes - MemoryCapacity.ArrayBackingBytes<ShellValue>(fields.Capacity));
        Memory.Workspace.TrimExcess();
        if (HasPendingExpansionError)
            PublishAssignmentTargetFailure(scope);
        else if (lastAssignment is { } assignmentArgument)
        {
            if (assignmentArgument.Value.HasValue)
            {
                Memory.Charge(256);
                lastArgument = await ExpandWordValueAsync(assignmentArgument.ToWord(), scope).ConfigureAwait(false);
            }
            else
                lastArgument = assignmentArgument.Name;
        }
        return new PreparedDeclaration(words == null ? command : command with { Words = words }, options, operands, lastArgument);

        PreparedDeclarationOperand PrepareVariableOperand(ShellValue input) =>
            AssignmentWord.TrySplitOperand(ShellSource.FromBytes(input.Bytes), Memory.Workspace, out var name, out var value, out var operation)
                ? new PreparedDeclarationOperand(name.ToValue(), value.ToValue(), operation, CompoundApplied: false)
                : new PreparedDeclarationOperand(input, null, AssignmentOperator.Replace, CompoundApplied: false);

        void AddField(ShellValue field, Word? sourceWord, int remainingFields)
        {
            lastArgument = field;
            lastAssignment = null;
            if (words != null)
            {
                if (sourceWord != null)
                    AddOperand(words, sourceWord);
                else
                    CaptureArgument(new PreparedDeclarationOperand(field, null, AssignmentOperator.Replace, CompoundApplied: false));
            }
            if (options.TryConsume(field))
                return;
            // Once option scanning stops, every remaining field in this word is an operand.
            Memory.PrepareListCapacity(operands, remainingFields);
            operands.Add(PrepareVariableOperand(field));
        }

        void AddAssignment(PreparedDeclarationOperand operand)
        {
            lastAssignment = operand;
            CaptureArgument(operand);
            AddOperand(operands, operand);
        }

        void CaptureArgument(PreparedDeclarationOperand operand)
        {
            if (words == null)
                return;
            // Admit the word, its literal parts and its cached expansion before constructing them.
            Memory.Charge(256);
            AddOperand(words, operand.ToWord());
        }

        void ReadCompoundOptions(Word word)
        {
            // A parsed initializer takes scope and array kind from source options.
            // Once a source operand is reached, later options can still select
            // its scope and kind but cannot select value transforms.
            if (word.Parts is not [SourceWordPart { Kind: WordPartKind.Unquoted } source, ..]
                || source.Source.AsSpan() is not [(byte)'-' or (byte)'+', ..])
            {
                readCompoundTransforms = false;
                return;
            }
            if (word.Parts.Count == 1 && source.Source.AsSpan().SequenceEqual("--"u8))
            {
                readCompoundTransforms = false;
                return;
            }
            var sign = source.Source.AsSpan()[0];
            for (var index = 0; index < word.Parts.Count; index++)
            {
                var bytes = word.Parts[index].GetUnexpandedValue().Bytes.Span;
                foreach (var flag in index == 0 ? bytes[1..] : bytes)
                {
                    if (sign == (byte)'-')
                    {
                        if (flag == (byte)'g' && builtin is BuiltinKind.Declare or BuiltinKind.Local)
                            compoundGlobal = true;
                        if (flag == (byte)'A')
                            compoundArrayKind = ArrayAssignmentKind.Associative;
                        else if (flag == (byte)'a' && compoundArrayKind == ArrayAssignmentKind.Auto)
                            compoundArrayKind = ArrayAssignmentKind.Indexed;
                    }
                    if (!readCompoundTransforms)
                        continue;
                    var attribute = ReadCaseAttribute(flag);
                    if (attribute == BindingAttributes.None && flag is not ((byte)'i' or (byte)'I'))
                        continue;
                    if (compoundAttributeSign == 0)
                        compoundAttributeSign = sign;
                    compoundInteger |= flag == (byte)'i';
                    compoundInherit |= flag == (byte)'I';
                    compoundCase |= attribute;
                }
            }
        }

        CaseAttributeChange GetCompoundCaseChange(bool set)
        {
            if (compoundCase == BindingAttributes.None)
                return default;
            if (!set)
                return new(BindingAttributes.None, compoundCase);
            return compoundCase is BindingAttributes.Capitalize or BindingAttributes.Lowercase or BindingAttributes.Uppercase
                ? default(CaseAttributeChange).Include(compoundCase, remove: false)
                : new(compoundCase, BindingAttributes.Case);
        }

        static bool HasConflictingArrayKind(Variables variables, BindingName name, ArrayKind kind) =>
            kind == ArrayKind.Associative ? variables.IndexedArrays.ContainsKey(name) : variables.AssociativeArrays.ContainsKey(name);
    }

    private bool TryParseDeclarationReference(ShellSource source, out VariableReference reference)
        => VariableReference.TryParse(source, Memory, out reference);

    internal Task ReportCircularNameReferenceAsync(BindingName name) =>
        ReportExpansionDiagnosticAsync([ShellValue.FromText("warning")],
            [GetDiagnosticName(name), ShellValue.FromText(": circular name reference")]);

    internal Task ReportRemovingNamerefAsync(BindingName name) =>
        ReportExpansionDiagnosticAsync([ShellValue.FromText("warning")],
            [GetDiagnosticName(name), ShellValue.FromText(": removing nameref attribute")]);

    private static string ArrayConversionMessage(ArrayKind kind) => kind == ArrayKind.Associative
        ? "cannot convert indexed to associative array" : "cannot convert associative to indexed array";

    /// <summary>Null Value means a name without an explicit value, not an assigned empty value.</summary>
    private readonly record struct PreparedDeclarationOperand(ShellValue Name, ShellValue? Value, AssignmentOperator Operator, bool CompoundApplied)
    {
        // Frozen pieces reuse ordinary word flattening only when dispatch or inspection needs a joined value.
        public Word ToWord() => new(Value is { } value
            ? [WordPart.FromValue(WordPartKind.SingleQuoted, Name),
                WordPart.FromValue(WordPartKind.SingleQuoted, ShellValue.FromText(Operator == AssignmentOperator.Append ? "+=" : "=")),
                WordPart.FromValue(WordPartKind.SingleQuoted, value)]
            : [WordPart.FromValue(WordPartKind.SingleQuoted, Name)]);
    }

    private sealed record PreparedDeclaration(
        SimpleCommand Command, DeclarationOptions Options, IReadOnlyList<PreparedDeclarationOperand> Operands, ShellValue? LastArgument);

    private enum DeclarationAttributeChange { Unchanged, Set, Clear }

    /// <summary>Positive options select listing attributes even when a later option clears mutation intent.</summary>
    [Flags]
    private enum DeclarationListingAttributes
    {
        None = 0, Indexed = 1, Associative = 2, Nameref = 4, Exported = 8, Readonly = 16, Integer = 32
    }

    private enum DeclarationHelp { None, FirstField, AfterOptions }

    private static BindingAttributes ReadCaseAttribute(byte flag) => flag switch
    {
        (byte)'c' => BindingAttributes.Capitalize,
        (byte)'l' => BindingAttributes.Lowercase,
        (byte)'u' => BindingAttributes.Uppercase,
        _ => BindingAttributes.None
    };

    /// <summary>Consumes only the initial option fields; later fields retain their operand meaning.</summary>
    private sealed class DeclarationOptions(BuiltinKind builtin)
    {
        private bool _readingOptions = true;
        private bool _seenOption;
        public BuiltinKind Builtin { get; } = builtin;
        public DeclarationAttributeChange IndexedChange { get; private set; }
        public DeclarationAttributeChange AssociativeChange { get; private set; }
        public ArrayAssignmentKind ArrayKind => AssociativeChange == DeclarationAttributeChange.Set
            ? ArrayAssignmentKind.Associative : IndexedChange == DeclarationAttributeChange.Set
                ? ArrayAssignmentKind.Indexed : ArrayAssignmentKind.Auto;
        public DeclarationAttributeChange NamerefChange { get; private set; }
        public DeclarationAttributeChange IntegerChange { get; private set; }
        public CaseAttributeChange CaseChange { get; private set; }
        public DeclarationAttributeChange ExportChange { get; private set; } = builtin == BuiltinKind.Export
            ? DeclarationAttributeChange.Set : DeclarationAttributeChange.Unchanged;
        public DeclarationAttributeChange ReadonlyChange { get; private set; } = builtin == BuiltinKind.Readonly
            ? DeclarationAttributeChange.Set : DeclarationAttributeChange.Unchanged;
        public bool Nameref => NamerefChange == DeclarationAttributeChange.Set && ArrayKind == ArrayAssignmentKind.Auto;
        public bool Readonly => ReadonlyChange == DeclarationAttributeChange.Set;
        public bool Global { get; private set; }
        public bool Inherit { get; private set; }
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
        public DeclarationHelp Help { get; private set; }

        public bool IsLocal(ScopedEvaluation scope, bool global) => !global
            && (Builtin is BuiltinKind.Local or BuiltinKind.Declare) && scope.Context.FunctionContext != null;

        public void EndOptions() => _readingOptions = false;

        public bool TryConsume(ShellValue field)
        {
            if (!_readingOptions)
                return false;
            var bytes = field.Bytes.Span;
            if (bytes.SequenceEqual("--"u8))
            {
                EndOptions();
                return true;
            }
            if (bytes.SequenceEqual("--help"u8))
            {
                if (Help == DeclarationHelp.None && InvalidOption.IsEmpty)
                    Help = _seenOption ? DeclarationHelp.AfterOptions : DeclarationHelp.FirstField;
                _seenOption = true;
                return true;
            }
            var remove = bytes.Length > 0 && bytes[0] == (byte)'+' && Builtin is BuiltinKind.Declare or BuiltinKind.Local;
            if (bytes.Length < 2 || bytes[0] != (byte)'-' && !remove)
            {
                EndOptions();
                return false;
            }
            _seenOption = true;

            // The exact long spellings were handled above. A dash after either sign is invalid.
            if (bytes[1] == (byte)'-')
            {
                if (InvalidOption.IsEmpty)
                    InvalidOption = ShellValue.FromBytes(field.Bytes[..2]);
                return true;
            }
            foreach (var flag in bytes[1..])
            {
                if (!remove && InvalidFunctionAttribute.Length == 0
                    && (flag is (byte)'a' or (byte)'A' or (byte)'i' || flag == (byte)'n' && Builtin != BuiltinKind.Export))
                    InvalidFunctionAttribute = "-" + (char)flag;
                switch (flag)
                {
                    case (byte)'a': IndexedChange = ReadChange(IndexedChange, DeclarationListingAttributes.Indexed); break;
                    case (byte)'A': AssociativeChange = ReadChange(AssociativeChange, DeclarationListingAttributes.Associative); break;
                    case (byte)'n' when Builtin == BuiltinKind.Export: ExportChange = DeclarationAttributeChange.Clear; break;
                    case (byte)'n' when Builtin == BuiltinKind.Readonly:
                        // Preserve existing attributes and the readonly filter for listings without operands.
                        ReadonlyChange = DeclarationAttributeChange.Unchanged;
                        break;
                    case (byte)'n' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: NamerefChange = ReadChange(NamerefChange, DeclarationListingAttributes.Nameref); break;
                    case (byte)'r' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: ReadonlyChange = ReadChange(ReadonlyChange, DeclarationListingAttributes.Readonly); break;
                    case (byte)'x' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: ExportChange = ReadChange(ExportChange, DeclarationListingAttributes.Exported); break;
                    case (byte)'i' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: IntegerChange = ReadChange(IntegerChange, DeclarationListingAttributes.Integer); break;
                    case (byte)'c' or (byte)'l' or (byte)'u' when Builtin is BuiltinKind.Declare or BuiltinKind.Local:
                        CaseChange = CaseChange.Include(ReadCaseAttribute(flag), remove);
                        break;
                    case (byte)'I' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: Inherit = !remove; break;
                    case (byte)'g' when Builtin is BuiltinKind.Declare or BuiltinKind.Local: Global |= !remove; break;
                    case (byte)'p': Print = true; break;
                    case (byte)'f':
                    case (byte)'F' when Builtin is BuiltinKind.Declare or BuiltinKind.Local:
                        FunctionNamesOnly |= flag == (byte)'F';
                        Functions |= !remove;
                        RemoveFunctions |= remove;
                        break;
                    default:
                        if (InvalidOption.IsEmpty) InvalidOption = ShellValue.FromBytes(new byte[] { bytes[0], flag });
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
