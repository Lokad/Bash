using System.Collections.Generic;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a redirection on a simple or compound command.
/// </summary>
public sealed class Redirection
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Redirection"/> class.
    /// </summary>
    /// <param name="kind">The redirection operator kind.</param>
    /// <param name="fileDescriptor">An optional file descriptor override.</param>
    /// <param name="target">The target word, or completed here-document body.</param>
    /// <param name="variableName">The optional variable name for descriptor allocation.</param>
    /// <param name="hereDocumentDelimiter">The delimiter of a completed here-document; null otherwise.</param>
    public Redirection(RedirectionKind kind, int? fileDescriptor, Word target, string variableName, Word? hereDocumentDelimiter)
    {
        Kind = kind;
        FileDescriptor = fileDescriptor;
        Target = target;
        VariableName = variableName;
        HereDocumentDelimiter = hereDocumentDelimiter;
        DescriptorOperand = target;
        if (kind is RedirectionKind.DuplicateInput or RedirectionKind.DuplicateOutput)
        {
            MovesDescriptor = TryTakeMoveSuffix(target, out var operand);
            DescriptorOperand = operand;
        }

        static bool TryTakeMoveSuffix(Word word, out Word operand)
        {
            operand = word;
            if (word.Parts.Count == 0) return false;
            var last = word.Parts[^1];
            if (last.Kind is not (WordPartKind.Unquoted or WordPartKind.Escaped)) return false;
            var value = last.GetUnexpandedValue();
            if (value.Bytes.IsEmpty || value.Bytes.Span[^1] != (byte)'-') return false;
            if (word.Parts.Count == 1 && value.Bytes.Length == 1 && last.Kind == WordPartKind.Unquoted)
                return false;
            var parts = new List<WordPart>(word.Parts.Count);
            for (var index = 0; index < word.Parts.Count - 1; index++) parts.Add(word.Parts[index]);
            if (value.Bytes.Length > 1)
                parts.Add(last is SourceWordPart source
                    ? WordPart.FromSource(last.Kind, source.Source.Slice(0, source.Source.Length - 1))
                    : WordPart.FromValue(last.Kind, ShellValue.FromBytes(value.Bytes[..^1])));
            operand = new Word(parts);
            return true;
        }
    }

    /// <summary>
    /// Gets the redirection operator kind.
    /// </summary>
    public RedirectionKind Kind { get; }

    /// <summary>
    /// Gets the optional file descriptor that the redirection applies to.
    /// </summary>
    public int? FileDescriptor { get; }

    /// <summary>
    /// Gets the target word, or completed here-document body.
    /// </summary>
    public Word Target { get; }

    /// <summary>
    /// Gets the optional variable name used for descriptor allocation.
    /// </summary>
    public string VariableName { get; }

    /// <summary>Retains the parsed delimiter separately from the completed body in Target.</summary>
    public Word? HereDocumentDelimiter { get; }

    /// <summary>Source-final move syntax is prepared once; expanded digits followed by '-' remain ordinary words.</summary>
    internal bool MovesDescriptor { get; }
    internal Word DescriptorOperand { get; }
}
