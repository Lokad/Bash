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
        SimpleCommand command, CommandWords sourceWords, ScopedEvaluation scope, BuiltinKind builtin,
        bool captureCommandWords)
    {
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
                    var globalInFunction = compoundGlobal && scope.Context.FunctionContext != null;
                    var setCompoundTransforms = compoundAttributeSign != (byte)'+'
                        || compoundGlobal || compoundArrayKind != ArrayAssignmentKind.Auto;
                    var inherit = compoundInherit && setCompoundTransforms;
                    var existingLocal = scope.Context.FunctionContext?.Locals.Declares(declaredName) == true;
                    VariableReference? literalTarget = null;
                    BindingName literalName = default;
                    VariableBinding? globalDeclaration = null;
                    var resolved = SelectTarget(out var name, out var targetVariables, out var targetLocal, out var targetReference);
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
                        resolved = SelectTarget(out name, out targetVariables, out targetLocal, out targetReference);
                        var expansionKind = kind;
                        kind = SelectKind();
                        if (expansionKind != kind && kind == ArrayKind.Associative)
                            await ReclassifyAssociativeItemsAsync(expandedItems).ConfigureAwait(false);
                        expanded = expandedItems;
                    }

                    if (!resolved)
                    {
                        globalDeclaration = null;
                        // Compound RHS expansion precedes both lookup warnings and fallback creation.
                        var warningPrefix = GetCommandDiagnosticPrefix(scope);
                        await ReportCircularNameReferenceAsync(declaredName, warningPrefix).ConfigureAwait(false);
                        if (local)
                            await ReportCircularNameReferenceAsync(declaredName, warningPrefix).ConfigureAwait(false); // Local binding creation.
                        else if (compoundArrayKind == ArrayAssignmentKind.Auto)
                        {
                            // Implicit array kind first attempts ordinary binding, then array lookup.
                            await ReportCircularNameReferenceAsync(declaredName, warningPrefix).ConfigureAwait(false);
                            await ReportCircularNameReferenceAsync(declaredName, warningPrefix).ConfigureAwait(false);
                        }
                        if (!local || existingLocal)
                            targetVariables.RecreateDeclarationArray(name, kind);
                    }

                    // Declaration operands expand before readonly/type rejection, unlike direct assignments.
                    var referencedElement = bracket < 0 && targetReference.Kind != VariableReferenceKind.Name;
                    var literalLocal = local && existingLocal && !name.IsIdentifier;
                    var writesLiteralGlobal = globalInFunction && bracket < 0 && globalDeclaration is { } globalTarget
                        && (globalTarget.Reference.Kind != VariableReferenceKind.Name || !globalTarget.Name.IsIdentifier);
                    var memberAssignment = bracket >= 0 || !writesLiteralGlobal && !literalLocal && !name.IsIdentifier
                        || !writesLiteralGlobal && (compoundGlobal
                        ? targetVariables.TryResolveBinding(declaredName, selectOwner: null, out var selected)
                            && selected.Reference.Kind != VariableReferenceKind.Name
                        : !local && referencedElement);
                    if (!memberAssignment && !local && !referencedElement && compoundArrayKind != ArrayAssignmentKind.Auto
                        && targetVariables.TryGetNameref(name, out _))
                        targetVariables.RecreateDeclarationArray(name, kind);
                    var declarationBinding = globalDeclaration
                        ?? (compoundGlobal && scope.Context.TryResolveGlobalBinding(declaredName, scope.Variables, out var globalBinding)
                            ? globalBinding : new VariableBinding(VariableReference.ForName(name), targetVariables));
                    var explicitNonlocalArray = !local && compoundArrayKind != ArrayAssignmentKind.Auto;
                    var conflictingKind = !local && (!compoundGlobal || explicitNonlocalArray)
                        && HasConflictingArrayKind(declarationBinding.Owner, declarationBinding.Name, kind);
                    var initializesLocalNameref = targetLocal && !memberAssignment && targetReference.Kind == VariableReferenceKind.Name
                        && targetVariables.IsUnsetNameref(targetReference.Name);
                    var redeclaresArrayNameref = targetLocal && !memberAssignment && targetVariables.IsArrayNameref(name);
                    var retainsArrayNameref = redeclaresArrayNameref && !HasConflictingArrayKind(targetVariables, name, kind);
                    var rejectsTarget = memberAssignment || !writesLiteralGlobal && (!local || targetLocal)
                        && (initializesLocalNameref ? targetVariables.IsReadonlyBinding(name) : targetVariables.IsReadonly(name));
                    if (conflictingKind && (explicitNonlocalArray || !rejectsTarget))
                    {
                        var namesElement = compoundGlobal ? declarationBinding.Reference.Kind != VariableReferenceKind.Name : referencedElement;
                        var subject = (namesElement ? declarationBinding.Name
                            : compoundArrayKind == ArrayAssignmentKind.Auto ? name : declaredName).ToValue();
                        var diagnosticName = _state.PreparationDiagnosticName;
                        ReadOnlySpan<ShellValue> diagnosticSubject = diagnosticName.IsEmpty
                            ? [subject] : [diagnosticName, ShellValue.FromText(": "), subject];
                        ReadOnlySpan<ShellValue> diagnosticMessage = [ShellValue.FromText(ArrayConversionMessage(kind))];
                        if (globalInFunction)
                        {
                            await ReportCommandDiagnosticAsync(diagnosticSubject, diagnosticMessage, scope).ConfigureAwait(false);
                            AddAssignment(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: false));
                            continue;
                        }
                        await ReportFatalExpansionAsync(diagnosticSubject, diagnosticMessage, ExpansionFailureKind.AssignmentTarget,
                            GetCommandDiagnosticPrefix(scope)).ConfigureAwait(false);
                        break;
                    }
                    var rejectsElementReference = !local && referencedElement && !globalInFunction;
                    var initializesReadonlyNameref = (initializesLocalNameref || retainsArrayNameref) && rejectsTarget;
                    var checksLocalArrayKind = local && targetLocal && !memberAssignment
                        && compoundArrayKind != ArrayAssignmentKind.Auto && HasConflictingArrayKind(targetVariables, name, kind);
                    if ((rejectsTarget && !redeclaresArrayNameref && !checksLocalArrayKind || rejectsElementReference)
                        && !initializesReadonlyNameref)
                    {
                        var owner = declarationBinding.Owner;
                        var arrayName = declarationBinding.Name;
                        // Source attributes belong to the declaration phase, before the initializer attempts to write.
                        var declared = rejectsElementReference && !explicitNonlocalArray
                            ? await PrepareImplicitElementArrayAsync(new VariableBinding(targetReference, owner), scope,
                                owner, assignUnassigned: (builtin is BuiltinKind.Declare or BuiltinKind.Local)
                                    && !compoundGlobal && !compoundInteger && compoundCase == BindingAttributes.None)
                                .ConfigureAwait(false)
                            : explicitNonlocalArray && (!referencedElement || !globalInFunction)
                                && owner.TryDeclareResolvedArray(arrayName, kind, ScalarArrayPromotion.RetainValue);
                        if (HasPendingExpansionError)
                            break;
                        if (declared)
                            ApplyCompoundAttributes(owner, arrayName, setCompoundTransforms);

                        if (!rejectsElementReference || !declared)
                            await ReportAssignmentTargetFailureAsync(assignment.Target.ToValue(),
                                    memberAssignment || rejectsElementReference ? "cannot assign list to array member" : "readonly variable", scope)
                                .ConfigureAwait(false);
                        else
                        {
                            var element = targetReference.ToValue(Memory);
                            var open = ShellValue.FromText("`");
                            var close = ShellValue.FromText("'");
                            var diagnosticName = _state.PreparationDiagnosticName;
                            ReadOnlySpan<ShellValue> subject = diagnosticName.IsEmpty ? [open, element, close]
                                : [diagnosticName, ShellValue.FromText(": "), open, element, close];
                            await ReportAssignmentTargetFailureAsync(subject,
                                [ShellValue.FromText("not a valid identifier")], scope).ConfigureAwait(false);
                        }
                        break;
                    }
                    if (initializesLocalNameref && initializesReadonlyNameref)
                        targetVariables.DetachNamerefToUnassigned(name);
                    else if (local && scope.Context.FunctionContext is { } function)
                    {
                        // A chain ending at an unset reference declares the array under the authored local name.
                        var declarationKind = initializesLocalNameref
                            ? kind == ArrayKind.Associative ? ArrayAssignmentKind.Associative : ArrayAssignmentKind.Indexed
                            : compoundArrayKind;
                        var localReference = literalLocal ? VariableReference.ForName(name) : declaredReference;
                        var declarationResult = function.Locals.TryDeclare(scope.Variables, scope.Variables,
                                new VariableOperand(VariableOperandKind.Name, localReference, ShellValue.Empty), prefixes: null,
                                declarationKind, isNameref: false, inherit, caseChange: default, elementAttributes: default,
                                Memory, function.Enclosing);
                        if (declarationResult != LocalDeclarationResult.Declared)
                        {
                            var kindConflict = declarationResult == LocalDeclarationResult.IncompatibleArrayKind;
                            var subject = (literalLocal || kindConflict && inherit ? declaredName : name).ToValue();
                            var diagnosticName = _state.PreparationDiagnosticName;
                            ReadOnlySpan<ShellValue> diagnosticSubject = diagnosticName.IsEmpty
                                ? [subject] : [diagnosticName, ShellValue.FromText(": "), subject];
                            var message = !kindConflict && !redeclaresArrayNameref && targetVariables.IsReadonly(name)
                                ? "readonly variable" : ArrayConversionMessage(kind);
                            await ReportCommandDiagnosticAsync(diagnosticSubject, [ShellValue.FromText(message)], scope).ConfigureAwait(false);
                            // Dispatch still visits this name and later operands; a shadowing function can also run.
                            AddAssignment(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: false));
                            continue;
                        }
                        if (inherit && !targetLocal && targetVariables.IsReadonlyBinding(name))
                        {
                            await ReportAssignmentTargetFailureAsync(assignment.Target.ToValue(), "readonly variable", scope)
                                .ConfigureAwait(false);
                            break;
                        }
                    }
                    else if (targetVariables.IsUnsetNameref(name))
                    {
                        if (compoundArrayKind == ArrayAssignmentKind.Auto)
                            await ReportRemovingNamerefAsync(name).ConfigureAwait(false);
                        targetVariables.TryRemoveNamerefAttribute(name);
                    }
                    if (writesLiteralGlobal)
                    {
                        // Prepare the global declaration first. A new literal binding gets its own
                        // attributes at dispatch, after the initializer has written its cells.
                        var owner = declarationBinding.Owner;
                        var baseName = declarationBinding.Name;
                        var declarationKind = compoundArrayKind == ArrayAssignmentKind.Auto
                            ? owner.GetArrayMetadata(baseName)?.Kind ?? ArrayKind.Indexed : kind;
                        if (!owner.TryDeclareResolvedArray(baseName, declarationKind, ScalarArrayPromotion.RetainValue))
                        {
                            await ReportAssignmentTargetFailureAsync(declaredValue, ArrayConversionMessage(declarationKind), scope)
                                .ConfigureAwait(false);
                            break;
                        }
                        ApplyCompoundAttributes(owner, baseName, setCompoundTransforms);
                        if (targetVariables.IsReadonlyBinding(name))
                        {
                            await ReportAssignmentTargetFailureAsync(declaredValue, "readonly variable", scope).ConfigureAwait(false);
                            break;
                        }
                    }
                    else
                        ApplyCompoundAttributes(targetVariables, name, setCompoundTransforms);
                    var appliedItems = await AwaitWordExpansionAsync(ApplyCompoundArrayItemsAsync(
                            name, assignment.Operator, kind, expanded, scope, scope, targetVariables, declaredName,
                            initializeReadonly: initializesReadonlyNameref, retainArrayNameref: retainsArrayNameref)).ConfigureAwait(false);
                    if (initializesLocalNameref && targetVariables.GetArrayMetadata(name) != null)
                    {
                        // The array keeps its cells and carries the nameref attribute on the same slot.
                        targetVariables.UpdateBindingAttributes(name, BindingAttributes.ArrayNameref, BindingAttributes.None);
                        scope.Context.FunctionContext?.Locals.Publish(name, targetVariables);
                    }
                    if (!appliedItems || initializesReadonlyNameref)
                    {
                        if (!HasPendingExpansionError)
                            SetFatalExpansionError(ExpansionFailureKind.AssignmentTarget);
                        break;
                    }

                    // Dispatch still sees a name. When expanded options select another scope or type,
                    // apply the ordinary named declaration there without repeating the initializer.
                    var applied = compoundGlobal == options.Global && compoundArrayKind == options.ArrayKind;
                    AddAssignment(new PreparedDeclarationOperand(declaredValue, null, assignment.Operator, CompoundApplied: applied));

                    bool SelectTarget(out BindingName targetName, out Variables variables, out bool isLocal,
                        out VariableReference targetReference)
                    {
                        var declaredOwner = compoundGlobal ? scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables) : null;
                        globalDeclaration = globalInFunction && bracket < 0 && declaredOwner?.TryGetNameref(declaredName, out _) == true
                            && scope.Context.TryResolveGlobalBinding(declaredName, scope.Variables, out var declaration)
                            ? declaration : null;
                        if (globalDeclaration is { } literal && (literal.Reference.Kind != VariableReferenceKind.Name || !literal.Name.IsIdentifier))
                        {
                            targetName = literal.Reference.Kind == VariableReferenceKind.Name
                                ? literal.Name : GetLiteralName(literal.Reference);
                            // An existing visible literal can receive the write even when preparation
                            // selected a hidden global with the same spelling.
                            variables = scope.Variables.HasBindingOrAttributes(targetName) ? scope.Variables
                                : literal.Reference.Kind == VariableReferenceKind.Name ? literal.Owner
                                : scope.Context.GetGlobalBindingOwner(targetName, scope.Variables);
                            targetReference = literal.Reference;
                            isLocal = false;
                            return true;
                        }
                        // -g selects the declaration's initial binding globally. Compound writes
                        // through a nameref then follow its referent in the active scope.
                        var found = declaredOwner != null
                            ? declaredOwner.TryResolveBinding(declaredName, _ => scope.Variables, out var binding)
                            : scope.Variables.TryResolveBinding(declaredName, selectOwner: null, out binding);
                        if (found && !local && compoundArrayKind != ArrayAssignmentKind.Auto)
                            binding = (declaredOwner ?? scope.Variables).SelectArrayDeclarationBinding(declaredName, binding);
                        targetName = !found || local && (!existingLocal || bracket < 0 && binding.IsUnsetNameref)
                            ? declaredName : binding.Name;
                        if (found && local && existingLocal && bracket < 0 && binding.Reference.Kind != VariableReferenceKind.Name)
                        {
                            // Use the complete target spelling for the local declaration and its initializer.
                            targetName = GetLiteralName(binding.Reference);
                        }
                        variables = declaredOwner == null ? scope.Variables : found ? binding.Owner : declaredOwner;
                        isLocal = local && scope.Context.FunctionContext?.Locals.Declares(targetName) == true;
                        targetReference = found ? binding.Reference : VariableReference.ForName(declaredName);
                        return found;
                    }

                    BindingName GetLiteralName(VariableReference reference)
                    {
                        // Reuse the admitted spelling if RHS expansion leaves the selector unchanged.
                        if (literalTarget != reference)
                        {
                            literalTarget = reference;
                            literalName = BindingNameExpansion.FromValue(reference.ToValue(Memory), Memory);
                        }
                        return literalName;
                    }

                    ArrayKind SelectKind() => compoundArrayKind switch
                    {
                        ArrayAssignmentKind.Associative => ArrayKind.Associative,
                        ArrayAssignmentKind.Indexed => ArrayKind.Indexed,
                        // In a function, -g checks the declared name's own array attribute before following a nameref.
                        _ when globalInFunction => scope.Context.GetGlobalBindingOwner(declaredName, scope.Variables)
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
                else if (WordExpansion.ExpandBraceAlternatives(word, scope) is { } alternatives)
                {
                    // Generated operands use ordinary argument expansion before declaration dispatch.
                    var expanded = await WordExpansion.ExpandBraceArgumentsAsync(alternatives, scope).ConfigureAwait(false);
                    if (HasPendingExpansionError
                        || !await AppendExpandedWordsAsync(expanded, fields, Memory.Workspace, static value => value)
                            .ConfigureAwait(false))
                        break;
                    AddExpandedFields();
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
            AddExpandedFields();
        }

        Memory.Workspace.Resize(Memory.Workspace.Bytes - MemoryCapacity.ArrayBackingBytes<ShellValue>(fields.Capacity));
        Memory.Workspace.TrimExcess();
        if (HasPendingExpansionError)
            PublishAssignmentTargetFailure(scope);
        else if (lastAssignment is { } assignmentArgument)
            lastArgument = assignmentArgument.Value.HasValue
                ? await GetDeclarationOperandValueAsync(assignmentArgument, scope).ConfigureAwait(false)
                : assignmentArgument.Name;
        return new PreparedDeclaration(words == null ? command : command with { Words = words }, options, operands, lastArgument);

        void AddExpandedFields()
        {
            if (words != null)
                Memory.PrepareListCapacity(words, fields.Count);
            for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                AddField(fields[fieldIndex], sourceWord: null, remainingFields: fields.Count - fieldIndex);
            fields.Clear();
        }

        PreparedDeclarationOperand PrepareVariableOperand(ShellValue input) =>
            !options.UsesCompleteOperands
            && AssignmentWord.TrySplitOperand(ShellSource.FromBytes(input.Bytes), Memory.Workspace, out var name, out var value, out var operation)
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

        void ApplyCompoundAttributes(Variables owner, BindingName name, bool set)
        {
            if (compoundInteger)
                owner.SetIntegerBinding(name, set);
            if (compoundCase == BindingAttributes.None)
                return;
            var change = compoundCase switch
            {
                _ when !set => new CaseAttributeChange(BindingAttributes.None, compoundCase),
                BindingAttributes.Capitalize or BindingAttributes.Lowercase or BindingAttributes.Uppercase
                    => default(CaseAttributeChange).Include(compoundCase, remove: false),
                _ => new CaseAttributeChange(compoundCase, BindingAttributes.Case),
            };
            owner.SetCaseAttributes(name, change);
        }
    }

    private static bool HasConflictingArrayKind(Variables variables, BindingName name, ArrayKind kind) =>
        kind == ArrayKind.Associative ? variables.IndexedArrays.ContainsKey(name) : variables.AssociativeArrays.ContainsKey(name);

    private async ValueTask<bool> PrepareImplicitElementArrayAsync(
        VariableBinding binding, ScopedEvaluation scope, Variables destination, bool assignUnassigned)
    {
        if (!assignUnassigned)
        {
            var kind = binding.Owner.GetArrayMetadata(binding.Name)?.Kind ?? ArrayKind.Indexed;
            return binding.Owner.TryDeclareResolvedArray(binding.Name, kind, ScalarArrayPromotion.RetainValue);
        }

        var target = await AwaitWordExpansionAsync(
            WordExpansion.ResolveDeclarationReferenceAsync(binding.Reference, scope, binding.Owner, scope.Variables)).ConfigureAwait(false);
        // A diagnosed invalid subscript can leave a valueless declaration successful.
        if (HasPendingExpansionError)
            return false;
        // A visible scalar prefix can select an indexed cell in a saved associative binding.
        if (target is WordExpansion.ResolvedVariableReference.Indexed indexed && !ReferenceEquals(binding.Owner, destination))
            target = indexed with { ReplaceAssociative = true };
        return target == null || target.TryDeclareUnassignedElement(destination, Memory);
    }

    private bool TryParseDeclarationReference(ShellSource source, out VariableReference reference)
        => VariableReference.TryParse(source, Memory, out reference);

    internal Task ReportCircularNameReferenceAsync(BindingName name, ScopedEvaluation scope) =>
        ReportCircularNameReferenceAsync(name, GetCommandDiagnosticPrefix(scope));

    private Task ReportCircularNameReferenceAsync(BindingName name, SyntaxDiagnosticPrefix prefix) =>
        WriteDiagnosticAsync([ShellValue.FromText("warning")], [GetDiagnosticName(name), ShellValue.FromText(": circular name reference")],
            _builtinDiagnostics, ClosedStderrHandling.AttemptWrite, prefix);

    internal Task ReportRemovingNamerefAsync(BindingName name) => ReportRemovingNamerefAsync(name, default);

    private Task ReportRemovingNamerefAsync(BindingName name, SyntaxDiagnosticPrefix prefix) =>
        WriteDiagnosticAsync([ShellValue.FromText("warning")],
            [GetDiagnosticName(name), ShellValue.FromText(": removing nameref attribute")],
            _builtinDiagnostics, ClosedStderrHandling.AttemptWrite, prefix);

    private static string ArrayConversionMessage(ArrayKind kind) => kind == ArrayKind.Associative
        ? "cannot convert indexed to associative array" : "cannot convert associative to indexed array";

    /// <summary>Reads the frozen operand bytes without constructing a second syntax word.</summary>
    private ValueTask<ShellValue> GetDeclarationOperandValueAsync(PreparedDeclarationOperand operand, ScopedEvaluation scope)
    {
        if (!scope.CanExpand)
            return new(ShellValue.Empty);
        CheckCancellation();
        if (operand.Value is not { } value)
            return new(operand.Name);

        var name = operand.Name.Bytes.Span;
        var separator = operand.AssignmentSeparator.Bytes.Span;
        try
        {
            var bytes = ExpandedWord.AllocateValue((long)name.Length + separator.Length + value.Bytes.Length, Memory);
            name.CopyTo(bytes);
            separator.CopyTo(bytes.AsSpan(name.Length));
            value.Bytes.Span.CopyTo(bytes.AsSpan(name.Length + separator.Length));
            return new(ShellValue.FromBytes(bytes));
        }
        catch (ByteMemoryBuilder.LimitExceededException error)
        {
            return new(AwaitWordExpansionAsync(Task.FromException<ShellValue>(error)));
        }
    }

    /// <summary>Null Value means a name without an explicit value, not an assigned empty value.</summary>
    private readonly record struct PreparedDeclarationOperand(ShellValue Name, ShellValue? Value, AssignmentOperator Operator, bool CompoundApplied)
    {
        private static readonly ShellValue Replacement = ShellValue.FromText("=");
        private static readonly ShellValue Append = ShellValue.FromText("+=");

        public ShellValue AssignmentSeparator => Operator == AssignmentOperator.Append ? Append : Replacement;

        // Retain a syntax word when the command can dispatch through the ordinary argument path.
        public Word ToWord() => new(Value is { } value
            ? [WordPart.FromValue(WordPartKind.SingleQuoted, Name),
                WordPart.FromValue(WordPartKind.SingleQuoted, AssignmentSeparator),
                WordPart.FromValue(WordPartKind.SingleQuoted, value)]
            : [WordPart.FromValue(WordPartKind.SingleQuoted, Name)]);
    }

    private sealed record PreparedDeclaration(
        SimpleCommand Command, DeclarationOptions Options, IReadOnlyList<PreparedDeclarationOperand> Operands, ShellValue? LastArgument);

    private enum DeclarationAttributeChange { Unchanged, Set, Clear }
    private enum DeclarationPrefixPromotion { Element, Nameref, Integer }

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
        public bool PromotesTemporaryBindings => Builtin == BuiltinKind.Declare && !Global
            && NamerefChange == DeclarationAttributeChange.Unchanged && ArrayKind == ArrayAssignmentKind.Auto
            && (ExportChange == DeclarationAttributeChange.Set || Readonly);
        public bool Global { get; private set; }
        public bool Inherit { get; private set; }
        public bool Print { get; private set; }
        public bool Functions { get; private set; }
        public bool PrintNamedVariables => Print && Builtin is BuiltinKind.Declare or BuiltinKind.Local;
        public bool UsesCompleteOperands => Functions || PrintNamedVariables;
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
