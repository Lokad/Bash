using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal static partial class WordExpansion
{
    private static readonly ShellValue DeclareWord = ShellValue.FromText("declare");
    private static readonly ShellValue SetWord = ShellValue.FromText("set");
    private static readonly ShellValue EndOptionsWord = ShellValue.FromText("--");
    private static readonly ShellValue PositionalAssignmentPrefix = ShellValue.FromText("set -- ");
    private static readonly ShellValue SpaceSeparator = ShellValue.FromText(" ");

    private static void ExpandInspectionTransform(
        byte transform, ParameterValue parameter, ScopedEvaluation scope, ExpandedWordBuilder output,
        bool allowWordSplitting, bool globQuoted, ExpansionQuoteContext quoteContext)
    {
        var assignmentContext = output.Purpose is ExpansionPurpose.Assignment
            or ExpansionPurpose.IndexedCompound or ExpansionPurpose.IndexedCompoundOperand;
        var scalarContext = output.Purpose == ExpansionPurpose.Scalar || assignmentContext;
        var hereDocument = quoteContext == ExpansionQuoteContext.HereDocument;
        var cancellationToken = scope.Effects.CancellationToken;
        var budget = scope.Effects.Memory.Budget;
        var kind = parameter.Kind;
        var positional = parameter.RangeSource is ParameterSource.Positional;
        if (transform == 'A' && positional)
        {
            if (parameter.IsSet)
                EmitPositionalAssignment();
            else
                Emit([]);
            return;
        }

        var entireArray = parameter.RangeSource is ParameterSource.Indexed or ParameterSource.Associative;
        if (transform is (byte)'K' or (byte)'k')
        {
            if (!parameter.IsSet)
                Emit(parameter.Fields);
            else if (entireArray)
            {
                var entries = parameter.RangeSource switch
                {
                    ParameterSource.Indexed indexed => indexed.Array.Entries,
                    ParameterSource.Associative associative => associative.Array.Entries,
                    _ => throw new InvalidOperationException("Expected an array parameter.")
                };
                if (transform == 'k')
                    EmitValues(PairFields(entries));
                else
                    EmitSerializedPairs(entries);
            }
            else
                EmitValues(QuoteFields(parameter.RangeSource?.Fields ?? parameter.Fields));
            return;
        }

        var binding = parameter.InspectionBinding;
        if (binding == null && parameter.Target is { } target && !target.Name.IsEmpty)
        {
            var owner = target.ReadOwner ?? scope.Variables;
            // A bare name that reaches an element still reads the value, but it has
            // no declaration of its own. Explicit subscripts use the array's declaration.
            if ((target.IsWholeBindingRead || parameter.Source?.Kind == VariableReferenceKind.Element)
                && !owner.TryGetNameref(target.Name, out _))
                binding = new VariableBinding(VariableReference.ForName(target.Name), owner);
        }
        var attributes = binding is { } selected
            ? selected.Owner.GetDeclarationAttributes(selected.Name)
            : ShellValue.Empty;
        if (transform == 'a')
        {
            if (entireArray)
            {
                var count = parameter.RangeSource switch
                {
                    ParameterSource.Indexed indexed => indexed.Array.IsInitialized ? indexed.Array.Count : 1,
                    ParameterSource.Associative associative => associative.Array.IsInitialized ? associative.Array.Count : 1,
                    _ => 0
                };
                EmitValues(RepeatAttributes(count));
            }
            else if (positional)
                EmitValues(RepeatAttributes(parameter.Fields.Count));
            else if (!attributes.IsEmpty || kind is not (VariableReferenceKind.At or VariableReferenceKind.Star) || parameter.IsSet)
                output.AppendExpansion(attributes, allowWordSplitting, globQuoted);
            else
                Emit([]);
            return;
        }

        if (binding is { } assignment && (!attributes.IsEmpty || parameter.IsSet))
            EmitAssignment(assignment, attributes, entireArray);
        else if (kind is VariableReferenceKind.At or VariableReferenceKind.Star)
            Emit([]);
        else
            output.AppendExpansion(ShellValue.Empty, allowWordSplitting, globQuoted);

        void Emit(IEnumerable<ShellValue> fields) =>
            AppendParameterFields(kind, fields, output, allowWordSplitting, globQuoted, quoteContext);

        void EmitValues(IEnumerable<ShellValue> fields)
        {
            if (hereDocument && kind is VariableReferenceKind.At or VariableReferenceKind.Star)
                output.AppendSeparatedFields(fields, SpaceSeparator, allowWordSplitting, globQuoted);
            else if (assignmentContext && kind == VariableReferenceKind.At)
                output.AppendJoinedFields(fields, allowWordSplitting, globQuoted);
            else
                Emit(fields);
        }

        IEnumerable<ShellValue> RepeatAttributes(int count)
        {
            for (var index = 0; index < count; index++)
                yield return attributes;
        }

        static IEnumerable<ShellValue> PairFields(IEnumerable<KeyValuePair<ShellValue, ShellValue?>> pairs)
        {
            foreach (var (key, value) in pairs)
            {
                yield return key;
                yield return value.GetValueOrDefault();
            }
        }

        IEnumerable<ShellValue> QuoteFields(IEnumerable<ShellValue> fields)
        {
            foreach (var field in fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var rendered = new ByteMemoryBuilder(0, scope.Effects.Memory.Workspace);
                AppendQuoted(rendered, field, ShellQuoteStyle.SingleQuoted);
                yield return ShellValue.FromBytes(rendered.DetachSnapshot());
            }
        }

        void EmitSerializedPairs(IEnumerable<KeyValuePair<ShellValue, ShellValue?>> pairs)
        {
            using var rendered = new ByteMemoryBuilder(0, budget);
            var associative = parameter.RangeSource is ParameterSource.Associative;
            foreach (var (key, value) in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!associative && rendered.Length != 0)
                    rendered.Append((byte)' ');
                AppendQuoted(rendered, key, associative ? ShellQuoteStyle.AssociativeKey : ShellQuoteStyle.Backslash);
                rendered.Append((byte)' ');
                AppendQuoted(rendered, value.GetValueOrDefault(), ShellQuoteStyle.DoubleQuoted);
                if (associative)
                    rendered.Append((byte)' ');
            }
            output.AppendExpansion(ShellValue.FromBytes(rendered.ToMemory()), allowWordSplitting, globQuoted);
        }

        void EmitAssignment(VariableBinding selected, ShellValue flags, bool entireArray)
        {
            using var rendered = new ByteMemoryBuilder(0, budget);
            var separators = FieldSeparators.FromVariables(scope.Variables);
            var separate = entireArray && !scalarContext
                && (kind == VariableReferenceKind.At && quoteContext == ExpansionQuoteContext.DoubleQuoted
                    && (separators.IsEmpty || separators.Bytes.Span.Contains((byte)' '))
                    || allowWordSplitting && separators.IsEmpty);
            if (!flags.IsEmpty)
            {
                if (!separate)
                    rendered.Append("declare "u8);
                rendered.Append((byte)'-').Append(flags.Bytes);
                if (!separate)
                    rendered.Append((byte)' ');
            }
            var prefixLength = rendered.Length;
            rendered.Append(selected.Name.ToValue().Bytes);
            if (entireArray)
            {
                if (!selected.Owner.TryAppendArrayAssignment(selected.Name, rendered, cancellationToken))
                    throw new ByteMemoryBuilder.LimitExceededException();
            }
            else if (parameter.IsSet)
            {
                rendered.Append((byte)'=');
                AppendQuoted(rendered, parameter.Value, ShellQuoteStyle.SingleQuoted);
            }
            var assignment = rendered.ToMemory();
            if (separate)
                output.AppendFields([DeclareWord, ShellValue.FromBytes(assignment[..prefixLength]), ShellValue.FromBytes(assignment[prefixLength..])],
                    allowWordSplitting, globQuoted, quoteContext);
            else
                output.AppendExpansion(ShellValue.FromBytes(assignment), allowWordSplitting, globQuoted);
        }

        void EmitPositionalAssignment()
        {
            var quoted = QuoteFields(parameter.Fields);
            if (scalarContext || hereDocument || kind == VariableReferenceKind.Star && !allowWordSplitting)
            {
                output.AppendExpansion(PositionalAssignmentPrefix, allowWordSplitting, globQuoted);
                EmitValues(quoted);
                return;
            }
            var separators = FieldSeparators.FromVariables(scope.Variables);
            if (!separators.IsEmpty && !separators.Bytes.Span.Contains((byte)' '))
            {
                output.AppendExpansion(PositionalAssignmentPrefix, allowWordSplitting, globQuoted);
                output.AppendFields(quoted, allowWordSplitting, globQuoted, quoteContext);
            }
            else
                output.AppendFields(PrefixedFields(quoted), allowWordSplitting, globQuoted, quoteContext);

            static IEnumerable<ShellValue> PrefixedFields(IEnumerable<ShellValue> values)
            {
                yield return SetWord;
                yield return EndOptionsWord;
                foreach (var value in values)
                    yield return value;
            }
        }

        static void AppendQuoted(ByteMemoryBuilder rendered, ShellValue value, ShellQuoteStyle style)
        {
            if (!ShellArgumentEscaping.TryAppendQuoted(rendered, value.Bytes.Span, style))
                throw new ByteMemoryBuilder.LimitExceededException();
        }
    }
}
