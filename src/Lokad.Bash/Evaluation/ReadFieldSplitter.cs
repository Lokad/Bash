using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Splits delimiter-free read input, preserving escapes and the last field's remainder.</summary>
/// <remarks>Fields borrow the input or use the supplied scratch budget. Detach borrowed slices and admit retained backing before assigning.</remarks>
internal static class ReadFieldSplitter
{
    internal static List<ShellValue> Split(ReadOnlyMemory<byte> content, int fieldCount, ReadBuiltinOptions options,
        MemoryCapacity scratch, Variables variables, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var fieldSeparators = options.FieldSeparators;
        const int FieldListOverheadBytes = 32;
        var capacity = options.FieldMode switch
        {
            ReadFieldMode.PreserveInput => 1,
            ReadFieldMode.SplitAll => 0,
            _ => fieldCount
        };
        scratch.Grow(FieldListOverheadBytes + MemoryCapacity.ArrayBackingBytes<ShellValue>(capacity));
        return SplitReadFields(content.Length);

        List<ShellValue> SplitReadFields(int contentEnd)
        {
            if (options.FieldMode == ReadFieldMode.PreserveInput)
                return [BuildValue(0, contentEnd)];

            var separatorBytes = fieldSeparators.Bytes;
            // Span searches specialize sets of up to three bytes.
            var asciiRaw = options.BackslashMode == ReadBackslashMode.Preserve
                && separatorBytes.Length is > 0 and <= 3 && Ascii.IsValid(separatorBytes.Span);
            Span<byte> whitespaceStorage = stackalloc byte[3];
            var whitespaceLength = 0;
            if (asciiRaw)
                foreach (var separator in separatorBytes.Span)
                    if (FieldSeparators.HasWhitespaceSemantics(new Rune(separator)))
                        whitespaceStorage[whitespaceLength++] = separator;
            var rawWhitespace = whitespaceStorage[..whitespaceLength];

            var splitFields = new List<ShellValue>(capacity);
            var position = 0;
            if (options.FieldMode == ReadFieldMode.SplitAll)
            {
                while (TryReadField(ref position, rawWhitespace, out var start, out var end))
                {
                    scratch.PrepareListCapacity(splitFields, 1);
                    splitFields.Add(BuildValue(start, end));
                }
                return splitFields;
            }

            for (var fieldIndex = 0; fieldIndex < fieldCount - 1; fieldIndex++)
            {
                splitFields.Add(TryReadField(ref position, rawWhitespace, out var start, out var end)
                    ? BuildValue(start, end)
                    : ShellValue.Empty);
            }

            if (!TryReadField(ref position, rawWhitespace, out var firstStart, out var firstEnd))
            {
                splitFields.Add(ShellValue.Empty);
            }
            else if (!TryReadCharacter(position, out _, out _))
            {
                splitFields.Add(BuildValue(firstStart, firstEnd));
            }
            else
            {
                splitFields.Add(BuildValue(firstStart, TrimRemainderEnd(firstStart, rawWhitespace)));
            }

            return splitFields;

            bool TryReadField(ref int index, ReadOnlySpan<byte> whitespace, out int start, out int end)
            {
                if (fieldSeparators.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    start = index;
                    end = index = contentEnd;
                    return start < end;
                }
                if (asciiRaw)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // An ASCII separator cannot occur inside a UTF-8 scalar.
                    var bytes = content.Span[..contentEnd];
                    var separators = separatorBytes.Span;
                    start = index + LeadingWhitespace(bytes[index..], whitespace);
                    var boundary = bytes[start..].IndexOfAny(separators);
                    if (boundary < 0)
                    {
                        end = index = contentEnd;
                        return start < end;
                    }

                    end = start + boundary;
                    index = end + LeadingWhitespace(bytes[end..], whitespace);
                    // Adjacent IFS whitespace and one other separator form a single delimiter.
                    if (whitespace.Length != separators.Length && index < contentEnd && separators.Contains(bytes[index]))
                    {
                        index++;
                        index += LeadingWhitespace(bytes[index..], whitespace);
                    }
                    return true;
                }

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
                cancellationToken.ThrowIfCancellationRequested();
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

            static int LeadingWhitespace(ReadOnlySpan<byte> remaining, ReadOnlySpan<byte> whitespace)
            {
                if (whitespace.IsEmpty)
                    return 0;
                var first = remaining.IndexOfAnyExcept(whitespace);
                return first < 0 ? remaining.Length : first;
            }

            int TrimRemainderEnd(int start, ReadOnlySpan<byte> whitespace)
            {
                // IFS whitespace is ASCII. Trim from the end so the remainder need not be scanned again.
                var bytes = content.Span;
                if (asciiRaw)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return whitespace.IsEmpty ? contentEnd : start + bytes[start..contentEnd].LastIndexOfAnyExcept(whitespace) + 1;
                }
                var end = contentEnd;
                while (end > start)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scalar = new Rune(bytes[end - 1]);
                    if (!FieldSeparators.HasWhitespaceSemantics(scalar)
                        || !IsSeparator(new ReadCharacter(content.Slice(end - 1, 1), scalar, IsEscaped: false)))
                        break;

                    end--;
                    if (options.BackslashMode == ReadBackslashMode.Preserve)
                        continue;
                    // Trim the escape of removed whitespace, retaining any preceding literal backslashes.
                    var escapeStart = end;
                    while (escapeStart > start && bytes[escapeStart - 1] == (byte)'\\')
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        escapeStart--;
                    }
                    if ((end - escapeStart) % 2 != 0)
                        end--;
                }
                return end;
            }

            bool IsIfsNonWhitespace(ReadCharacter character) =>
                IsIfsDelimiter(character) && !(character.Scalar is { } scalar && FieldSeparators.HasWhitespaceSemantics(scalar));

            bool IsSeparator(ReadCharacter character)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fieldSeparators = character.Scalar.HasValue
                    ? fieldSeparators.PrepareCharacters(variables, memory, cancellationToken)
                    : fieldSeparators.PrepareBytes(variables, memory, cancellationToken);
                return character.Scalar is { } scalar ? fieldSeparators.Contains(scalar)
                    : fieldSeparators.Contains(character.Bytes.Span[0]);
            }

            ShellValue BuildValue(int start, int valueEnd)
            {
                if (start >= valueEnd)
                    return ShellValue.Empty;

                var source = content[start..valueEnd];
                if (options.BackslashMode == ReadBackslashMode.Preserve)
                    return ShellValue.FromBytes(source);
                var remaining = source.Span;
                var runEnd = remaining.IndexOfAny((byte)'\\', (byte)0);
                if (runEnd < 0)
                    return ShellValue.FromBytes(source);

                scratch.Grow(remaining.Length);
                var value = new byte[remaining.Length];
                var written = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    remaining[..runEnd].CopyTo(value.AsSpan(written));
                    written += runEnd;
                    remaining = remaining[runEnd..];
                    // A quoted NUL can create a field but ends the assigned bytes.
                    if (remaining.Length <= 1 || remaining[0] == 0 || remaining[1] == 0)
                        break;
                    // Removing an escape preserves the rest of the original byte spelling.
                    value[written++] = remaining[1];
                    remaining = remaining[2..];
                    runEnd = remaining.IndexOfAny((byte)'\\', (byte)0);
                    if (runEnd < 0)
                        runEnd = remaining.Length;
                }

                return written == 0 ? ShellValue.Empty : ShellValue.FromBytes(value.AsMemory(0, written));
            }
        }
    }

    /// <summary>Retains input bytes and escape provenance; malformed bytes have no Unicode scalar.</summary>
    private readonly record struct ReadCharacter(ReadOnlyMemory<byte> Bytes, Rune? Scalar, bool IsEscaped);
}
