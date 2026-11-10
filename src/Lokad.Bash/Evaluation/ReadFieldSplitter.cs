using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Splits delimiter-free read input, preserving escapes and the last field's remainder.</summary>
/// <remarks>Fields borrow the input or use the supplied scratch budget. Copy them before retaining assignments.</remarks>
internal static class ReadFieldSplitter
{
    internal static List<ShellValue> Split(ReadOnlyMemory<byte> content, int fieldCount, ReadBuiltinOptions options,
        MemoryCapacity listMemory, MemoryCapacity decodedMemory)
    {
        const int FieldSlotBytes = 2 * sizeof(long);
        const int FieldListOverheadBytes = 64;
        var capacity = options.FieldMode == ReadFieldMode.PreserveInput ? 1 : fieldCount;
        listMemory.Grow(FieldListOverheadBytes + (long)FieldSlotBytes * capacity);
        return SplitReadFields(content.Length);

        List<ShellValue> SplitReadFields(int contentEnd)
        {
            if (options.FieldMode == ReadFieldMode.PreserveInput)
                return [BuildValue(0, contentEnd)];

            var splitFields = new List<ShellValue>(fieldCount);
            var position = 0;
            for (var fieldIndex = 0; fieldIndex < fieldCount - 1; fieldIndex++)
            {
                splitFields.Add(TryReadField(ref position, out var start, out var end)
                    ? BuildValue(start, end)
                    : ShellValue.Empty);
            }

            SkipIfsWhitespace(ref position);
            var remainderStart = position;
            var probePosition = position;
            if (!TryReadField(ref probePosition, out var firstStart, out var firstEnd))
            {
                splitFields.Add(ShellValue.Empty);
            }
            else if (!TryReadField(ref probePosition, out _, out _))
            {
                splitFields.Add(BuildValue(firstStart, firstEnd));
            }
            else
            {
                // Keep source offsets instead of a per-character table. Trim even escaped
                // trailing IFS whitespace, preserving the final-remainder semantics of read.
                var remainderEnd = remainderStart;
                var index = remainderStart;
                while (TryReadCharacter(index, out var character, out var next))
                {
                    if (!HasIfsWhitespaceValue(character))
                        remainderEnd = next;
                    index = next;
                }

                splitFields.Add(BuildValue(remainderStart, remainderEnd));
            }

            return splitFields;

            bool TryReadField(ref int index, out int start, out int end)
            {
                SkipIfsWhitespace(ref index);
                start = end = index;

                if (!TryReadCharacter(index, out var character, out var next))
                    return false;

                if (IsIfsNonWhitespace(character))
                {
                    ConsumeDelimiter(ref index);
                    return true;
                }

                do
                {
                    index = next;
                }
                while (TryReadCharacter(index, out character, out next) && !IsIfsDelimiter(character));

                end = index;
                ConsumeDelimiter(ref index);

                return true;
            }

            void ConsumeDelimiter(ref int index)
            {
                if (!TryReadCharacter(index, out var character, out var next))
                    return;

                if (IsIfsWhitespace(character))
                {
                    SkipIfsWhitespace(ref index);
                    if (!TryReadCharacter(index, out character, out next) || !IsIfsNonWhitespace(character))
                        return;
                }

                index = next;
                SkipIfsWhitespace(ref index);
            }

            void SkipIfsWhitespace(ref int index)
            {
                while (TryReadCharacter(index, out var character, out var next) && IsIfsWhitespace(character))
                    index = next;
            }

            bool TryReadCharacter(int index, out ReadCharacter character, out int next)
            {
                character = default;
                next = index;
                if (index >= contentEnd)
                    return false;

                var escaped = options.BackslashMode == ReadBackslashMode.InterpretEscapes
                    && content.Span[index] == (byte)'\\';
                if (escaped && ++index >= contentEnd)
                    return false;

                var status = Rune.DecodeFromUtf8(content.Span[index..contentEnd], out var scalar, out var length);
                if (status != OperationStatus.Done)
                    length = 1;
                next = index + length;
                character = new ReadCharacter(
                    content.Slice(index, length), status == OperationStatus.Done ? scalar : null, escaped);
                return true;
            }

            bool IsIfsDelimiter(ReadCharacter character) =>
                !character.IsEscaped && IsSeparator(character);

            bool IsIfsWhitespace(ReadCharacter character) =>
                IsIfsDelimiter(character) && character.Scalar is { } scalar && FieldSeparators.HasWhitespaceSemantics(scalar);

            bool HasIfsWhitespaceValue(ReadCharacter character) =>
                IsSeparator(character)
                && character.Scalar is { } scalar && FieldSeparators.HasWhitespaceSemantics(scalar);

            bool IsIfsNonWhitespace(ReadCharacter character) =>
                IsIfsDelimiter(character) && !(character.Scalar is { } scalar && FieldSeparators.HasWhitespaceSemantics(scalar));

            bool IsSeparator(ReadCharacter character) => character.Scalar is { } scalar
                ? options.FieldSeparators.Contains(scalar)
                : options.FieldSeparators.Contains(character.Bytes.Span[0]);

            ShellValue BuildValue(int start, int valueEnd)
            {
                if (start >= valueEnd)
                    return ShellValue.Empty;

                if (options.BackslashMode == ReadBackslashMode.Preserve
                    || !content.Span[start..valueEnd].Contains((byte)'\\'))
                {
                    return ShellValue.FromBytes(content[start..valueEnd]);
                }

                using var builder = new ByteMemoryBuilder(valueEnd - start, decodedMemory);
                var index = start;
                while (index < valueEnd && TryReadCharacter(index, out var character, out var next))
                {
                    builder.Append(character.Bytes);
                    index = next;
                }

                // A quoted NUL affects splitting before it terminates the assigned field.
                return ShellValue.FromBytes(builder.DetachSnapshot()).BeforeFirstNul();
            }
        }
    }

    /// <summary>Retains input bytes and escape provenance; malformed bytes have no Unicode scalar.</summary>
    private readonly record struct ReadCharacter(ReadOnlyMemory<byte> Bytes, Rune? Scalar, bool IsEscaped);
}
