using System;
using System.Numerics;

namespace Lokad.Bash.Evaluation;

/// <summary>Predicate vocabulary shared by the distinct [[ and test/[ grammars.</summary>
internal static class ShellPredicates
{
    internal enum UnaryPredicateKind { StringLengthNonZero, StringLengthZero, OptionEnabled, FilePredicate }
    internal enum BinaryPredicateKind
    {
        NumericEqual, NumericNotEqual, NumericLess, NumericLessOrEqual, NumericGreater,
        NumericGreaterOrEqual, StringEqual, StringNotEqual, StringLess, StringGreater,
        RegexMatch, FilePredicate
    }

    internal readonly record struct UnaryPredicate(UnaryPredicateKind Operator, FilePredicateKind FileKind)
    {
        public bool IsFilePredicate => Operator == UnaryPredicateKind.FilePredicate;
    }

    internal readonly record struct BinaryPredicate(BinaryPredicateKind Operator, FilePredicateKind FileKind)
    {
        public bool IsFilePredicate => Operator == BinaryPredicateKind.FilePredicate;
        public bool IsNumeric => Operator is >= BinaryPredicateKind.NumericEqual and <= BinaryPredicateKind.NumericGreaterOrEqual;
    }

    internal static bool TryGetUnaryPredicate<T>(ReadOnlySpan<T> token, out UnaryPredicate predicate)
        where T : IBinaryInteger<T>
    {
        var code = AsciiToken(token);
        UnaryPredicateKind? kind = code switch
        {
            ('-', 'n', '\0') => UnaryPredicateKind.StringLengthNonZero,
            ('-', 'z', '\0') => UnaryPredicateKind.StringLengthZero,
            ('-', 'o', '\0') => UnaryPredicateKind.OptionEnabled,
            _ => null
        };
        if (kind.HasValue)
        {
            predicate = new(kind.Value, default);
            return true;
        }
        FilePredicateKind? file = code switch
        {
            ('-', 'a', '\0') or ('-', 'e', '\0') => FilePredicateKind.Exists,
            ('-', 'b', '\0') => FilePredicateKind.BlockDevice,
            ('-', 'c', '\0') => FilePredicateKind.CharacterDevice,
            ('-', 'd', '\0') => FilePredicateKind.Directory,
            ('-', 'f', '\0') => FilePredicateKind.File,
            ('-', 'g', '\0') => FilePredicateKind.SetGroupId,
            ('-', 'h', '\0') or ('-', 'L', '\0') => FilePredicateKind.SymbolicLink,
            ('-', 'k', '\0') => FilePredicateKind.Sticky,
            ('-', 'p', '\0') => FilePredicateKind.Fifo,
            ('-', 'r', '\0') => FilePredicateKind.Readable,
            ('-', 's', '\0') => FilePredicateKind.NonEmpty,
            ('-', 't', '\0') => FilePredicateKind.Terminal,
            ('-', 'u', '\0') => FilePredicateKind.SetUserId,
            ('-', 'w', '\0') => FilePredicateKind.Writable,
            ('-', 'x', '\0') => FilePredicateKind.Executable,
            ('-', 'G', '\0') => FilePredicateKind.OwnedByEffectiveGroup,
            ('-', 'N', '\0') => FilePredicateKind.ModifiedSinceAccess,
            ('-', 'O', '\0') => FilePredicateKind.OwnedByEffectiveUser,
            ('-', 'S', '\0') => FilePredicateKind.Socket,
            _ => null
        };
        predicate = new(UnaryPredicateKind.FilePredicate, file.GetValueOrDefault());
        return file.HasValue;
    }

    internal static bool TryGetBinaryPredicate<T>(ReadOnlySpan<T> token, out BinaryPredicate predicate)
        where T : IBinaryInteger<T>
    {
        BinaryPredicateKind? kind = AsciiToken(token) switch
        {
            ('-', 'e', 'q') => BinaryPredicateKind.NumericEqual,
            ('-', 'n', 'e') => BinaryPredicateKind.NumericNotEqual,
            ('-', 'l', 't') => BinaryPredicateKind.NumericLess,
            ('-', 'l', 'e') => BinaryPredicateKind.NumericLessOrEqual,
            ('-', 'g', 't') => BinaryPredicateKind.NumericGreater,
            ('-', 'g', 'e') => BinaryPredicateKind.NumericGreaterOrEqual,
            ('=', '\0', '\0') or ('=', '=', '\0') => BinaryPredicateKind.StringEqual,
            ('!', '=', '\0') => BinaryPredicateKind.StringNotEqual,
            ('<', '\0', '\0') => BinaryPredicateKind.StringLess,
            ('>', '\0', '\0') => BinaryPredicateKind.StringGreater,
            ('=', '~', '\0') => BinaryPredicateKind.RegexMatch,
            _ => null
        };
        if (kind.HasValue)
        {
            predicate = new(kind.Value, default);
            return true;
        }
        FilePredicateKind? file = AsciiToken(token) switch
        {
            ('-', 'e', 'f') => FilePredicateKind.SameFile,
            ('-', 'n', 't') => FilePredicateKind.NewerThan,
            ('-', 'o', 't') => FilePredicateKind.OlderThan,
            _ => null
        };
        predicate = new(BinaryPredicateKind.FilePredicate, file.GetValueOrDefault());
        return file.HasValue;
    }

    internal static bool CompareNumbers(BinaryPredicateKind kind, long left, long right) => kind switch
    {
        BinaryPredicateKind.NumericEqual => left == right,
        BinaryPredicateKind.NumericNotEqual => left != right,
        BinaryPredicateKind.NumericLess => left < right,
        BinaryPredicateKind.NumericLessOrEqual => left <= right,
        BinaryPredicateKind.NumericGreater => left > right,
        BinaryPredicateKind.NumericGreaterOrEqual => left >= right,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static bool IsOptionEnabled(ReadOnlySpan<byte> name, bool errexitEnabled) =>
        name.SequenceEqual("pipefail"u8) || errexitEnabled && name.SequenceEqual("errexit"u8);

    private static (char, char, char) AsciiToken<T>(ReadOnlySpan<T> token) where T : IBinaryInteger<T>
    {
        if (token.Length is < 1 or > 3)
            return default;
        Span<char> characters = stackalloc char[3];
        characters.Clear();
        for (var index = 0; index < token.Length; index++)
        {
            var value = int.CreateChecked(token[index]);
            if (value is < 1 or > 127)
                return default;
            characters[index] = (char)value;
        }
        return (characters[0], characters[1], characters[2]);
    }
}
