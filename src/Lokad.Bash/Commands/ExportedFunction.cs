using System;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Commands;

/// <summary>A serialized function definition, separate from scalar environment variables.</summary>
/// <remarks>Callers must not mutate retained source bytes. The hosted shell validates before importing, never evaluates.</remarks>
public readonly record struct ExportedFunction(ReadOnlyMemory<byte> Source)
{
    /// <summary>Aggregate serialized-definition budget for one command invocation.</summary>
    public const int MaximumSourceBytes = ByteMemoryBuilder.MaxMemoryBuilderBytes - 1;
}
