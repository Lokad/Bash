namespace Lokad.Bash;

/// <summary>Identifies the operand implicated by a failed copy or rename.</summary>
public enum FileTransferOperand
{
    /// <summary>The failure involves both operands, or its operand cannot be established.</summary>
    Operation,
    /// <summary>The source or its traversal failed.</summary>
    Source,
    /// <summary>The destination or its traversal failed.</summary>
    Destination
}

/// <summary>An expected copy or rename failure, with operand attribution separate from its cause.</summary>
/// <remarks>
/// Hosts supply diagnostic detail without adding command or operand prefixes. Callers format the
/// original operands. Attribute only established facts; provider failures may concern the operation.
/// </remarks>
public readonly record struct FileTransferError(OperationError Error, FileTransferOperand Operand);
