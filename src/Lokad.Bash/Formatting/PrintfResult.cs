using System;
using System.Collections.Generic;

namespace Lokad.Bash.Formatting;

public sealed record PrintfResult(
    ReadOnlyMemory<byte> Output,
    IReadOnlyList<PrintfVariableAssignment> Assignments,
    IReadOnlyList<ShellValue> Diagnostics,
    int ExitCode);

public readonly record struct PrintfVariableAssignment(BindingName Name, int Count);
