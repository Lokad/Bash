using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Splits normalized read input, preserving escapes and the last field's remainder.</summary>
/// <remarks>Fields borrow the input or use the supplied scratch budget. Detach borrowed slices and admit retained backing before assigning.</remarks>
internal static class ReadFieldSplitter
{
    internal static List<ShellValue> Split(ReadOnlyMemory<byte> content, int fieldCount, ReadBuiltinOptions options,
        MemoryCapacity scratch, Variables variables, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var fieldSeparators = options.FieldSeparators;
        const int FieldListOverheadBytes = 32;
        // Each reached field consumes at least one record byte, including explicit empty fields.
        var capacity = options.FieldMode == ReadFieldMode.SplitAll ? 0 : Math.Min(fieldCount, content.Length);
        if (fieldSeparators.IsEmpty)
            capacity = Math.Min(capacity, 1);
        scratch.Grow(FieldListOverheadBytes + MemoryCapacity.ArrayBackingBytes<ShellValue>(capacity));
        return SplitReadFields(content.Length);

        List<ShellValue> SplitReadFields(int contentEnd)
        {
            var separatorBytes = fieldSeparators.Bytes;
            // Span searches specialize sets of up to three bytes.
            var shortAscii = separatorBytes.Length is > 0 and <= 3 && Ascii.IsValid(separatorBytes.Span);
            var interpretEscapes = options.BackslashMode == ReadBackslashMode.InterpretEscapes;
            Span<byte> boundaryStorage = stackalloc byte[4];
            scoped ReadOnlySpan<byte> boundaries = separatorBytes.Span;
            if (shortAscii && interpretEscapes && !boundaries.Contains((byte)'\\'))
            {
                boundaries.CopyTo(boundaryStorage);
                boundaryStorage[boundaries.Length] = (byte)'\\';
                boundaries = boundaryStorage[..(boundaries.Length + 1)];
            }

            Span<byte> whitespaceStorage = stackalloc byte[3];
            var whitespaceLength = 0;
            if (shortAscii)
                foreach (var separator in separatorBytes.Span)
                    if (FieldSeparators.HasWhitespaceSemantics(new Rune(separator)))
                        whitespaceStorage[whitespaceLength++] = separator;
            var asciiWhitespace = whitespaceStorage[..whitespaceLength];

            var splitFields = new List<ShellValue>(capacity);
            var position = 0;
            if (options.FieldMode == ReadFieldMode.SplitAll)
            {
                while (TryReadField(ref position, boundaries, asciiWhitespace, out var start, out var end))
                {
                    scratch.PrepareListCapacity(splitFields, 1);
                    splitFields.Add(BuildValue(start, end));
                }
                return splitFields;
            }

            for (var fieldIndex = 0; fieldIndex < fieldCount - 1; fieldIndex++)
            {
                if (!TryReadField(ref position, boundaries, asciiWhitespace, out var start, out var end))
                    return splitFields;
                splitFields.Add(BuildValue(start, end));
            }

            if (TryReadField(ref position, boundaries, asciiWhitespace, out var firstStart, out var firstEnd))
            {
                var end = TryReadCharacter(position, out _, out _)
                    ? TrimRemainderEnd(firstStart, asciiWhitespace) : firstEnd;
                splitFields.Add(BuildValue(firstStart, end));
            }

            return splitFields;

            bool TryReadField(ref int index, ReadOnlySpan<byte> scanBytes, ReadOnlySpan<byte> whitespace,
                out int start, out int end)
            {
                if (fieldSeparators.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    start = index;
                    end = index = contentEnd;
                    return start < end;
                }
                if (shortAscii)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // An ASCII separator cannot occur inside a UTF-8 scalar.
                    var bytes = content.Span[..contentEnd];
                    var separators = separatorBytes.Span;
                    start = index + LeadingWhitespace(bytes[index..], whitespace);
                    var boundary = interpretEscapes
                        ? FindUnescapedBoundary(bytes[start..], scanBytes)
                        : bytes[start..].IndexOfAny(separators);
                    if (boundary < 0)
                    {
                        end = index = contentEnd;
                        return start < end;
                    }

                    end = start + boundary;
                    index = end + LeadingWhitespace(bytes[end..], whitespace);
                    // Adjacent IFS whitespace and one other separator form a single delimiter.
                    if (whitespace.Length != separators.Length && index < contentEnd
                        && (!interpretEscapes || bytes[index] != (byte)'\\') && separators.Contains(bytes[index]))
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

                var remaining = content.Span[index..contentEnd];
                if (interpretEscapes && remaining[0] == (byte)'\\')
                {
                    if (remaining.Length == 1)
                        return false;
                    // The normalizer protects one byte. Any following UTF-8 continuation is separate.
                    character = new ReadCharacter(remaining[1], null, IsEscaped: true);
                    next = index + 2;
                    return true;
                }

                var status = Rune.DecodeFromUtf8(remaining, out var scalar, out var length);
                var valid = status == OperationStatus.Done;
                next = index + (valid ? length : 1);
                character = new ReadCharacter(remaining[0], valid ? scalar : null, IsEscaped: false);
                return true;
            }

            bool IsIfsDelimiter(ReadCharacter character) =>
                !character.IsEscaped && IsSeparator(character);

            bool IsIfsWhitespace(ReadCharacter character) =>
                IsIfsDelimiter(character) && character.Scalar is { } scalar && FieldSeparators.HasWhitespaceSemantics(scalar);

            int FindUnescapedBoundary(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> scanBytes)
            {
                const int ScanChunkBytes = 4096;
                var index = 0;
                while (index < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = bytes.Slice(index, Math.Min(bytes.Length - index, ScanChunkBytes));
                    var boundary = chunk.IndexOfAny(scanBytes);
                    if (boundary < 0)
                    {
                        index += chunk.Length;
                        continue;
                    }

                    index += boundary;
                    if (bytes[index] != (byte)'\\')
                        return index;
                    // Skip the escape and its next byte. UTF-8 continuation bytes cannot be ASCII boundaries.
                    index += 2;
                }
                return -1;
            }

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
                if (shortAscii && !interpretEscapes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return whitespace.IsEmpty ? contentEnd : start + bytes[start..contentEnd].LastIndexOfAnyExcept(whitespace) + 1;
                }
                var end = contentEnd;
                while (end > start)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsTrimWhitespace(end - 1, whitespace))
                        break;

                    end--;
                    if (!interpretEscapes)
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

            bool IsTrimWhitespace(int index, ReadOnlySpan<byte> whitespace)
            {
                var value = content.Span[index];
                if (shortAscii)
                    return whitespace.Contains(value);
                var scalar = new Rune(value);
                return FieldSeparators.HasWhitespaceSemantics(scalar)
                    && IsSeparator(new ReadCharacter(value, scalar, IsEscaped: false));
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
                    : fieldSeparators.Contains(character.FirstByte);
            }

            ShellValue BuildValue(int start, int valueEnd) => start >= valueEnd ? ShellValue.Empty
                : DecodeField(content[start..valueEnd], options.BackslashMode, scratch, cancellationToken);
        }
    }

    /// <summary>Decodes a selected field, borrowing unchanged bytes and admitting rewritten backing to scratch.</summary>
    internal static ShellValue DecodeField(ReadOnlyMemory<byte> source, ReadBackslashMode backslashMode,
        MemoryCapacity scratch, CancellationToken cancellationToken)
    {
        if (source.IsEmpty)
            return ShellValue.Empty;
        if (backslashMode == ReadBackslashMode.Preserve)
            return ShellValue.FromBytes(source);
        var remaining = source.Span;
        var runEnd = remaining.IndexOfAny((byte)'\\', (byte)0);
        if (runEnd < 0)
            return ShellValue.FromBytes(source);

        if (EndsField(remaining[runEnd..]))
        {
            // Only truncation is needed. The unchanged prefix can borrow the record.
            cancellationToken.ThrowIfCancellationRequested();
            return runEnd == 0 ? ShellValue.Empty : ShellValue.FromBytes(source[..runEnd]);
        }

        scratch.Grow(remaining.Length);
        var value = new byte[remaining.Length];
        var written = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remaining[..runEnd].CopyTo(value.AsSpan(written));
            written += runEnd;
            remaining = remaining[runEnd..];
            if (EndsField(remaining))
                break;
            // Removing an escape preserves the rest of the original byte spelling.
            value[written++] = remaining[1];
            remaining = remaining[2..];
            runEnd = remaining.IndexOfAny((byte)'\\', (byte)0);
            if (runEnd < 0)
                runEnd = remaining.Length;
        }

        return ShellValue.FromBytes(value.AsMemory(0, written));

        // A quoted NUL can create a field but ends the assigned bytes.
        static bool EndsField(ReadOnlySpan<byte> suffix) =>
            suffix.Length <= 1 || suffix[0] == 0 || suffix[1] == 0;
    }

    /// <summary>Retains the first byte and escape provenance; protected and malformed bytes have no Unicode scalar.</summary>
    private readonly record struct ReadCharacter(byte FirstByte, Rune? Scalar, bool IsEscaped);
}
