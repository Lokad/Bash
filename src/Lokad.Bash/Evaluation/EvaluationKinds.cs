namespace Lokad.Bash.Evaluation;

internal enum ShellForkKind { Subshell, CommandSubstitution, PipelineSimpleCommand }

internal enum ArrayAssignmentKind
{
    Auto,
    Indexed,
    Associative
}

internal enum DeclarationBindingScope { Visible, Global }

internal enum AssignmentContext
{
    Direct,
    Declaration,
    /// <summary>A global indexed declaration retains quoting from its associative local referent.</summary>
    QuotedIndexedDeclaration
}

internal enum DirectoryChangeKind
{
    None,
    ChangeDirectory,
    PushDirectory
}
