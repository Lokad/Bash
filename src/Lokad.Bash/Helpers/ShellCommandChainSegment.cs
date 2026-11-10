using System.Collections.Generic;

namespace Lokad.Bash.Helpers;

/// <summary>
/// A source-preserving display segment from <see cref="ShellCommandParser.TryParseChain"/>.
/// Usually one pipeline; headers sharing pending here-document bodies stay together.
/// </summary>
/// <param name="Text">The segment's own source text, with the separator that follows it stripped.</param>
/// <param name="Stages">
/// The segment's flattened pipeline stages when it is a plain simple command or pipe of simple
/// commands; <c>null</c> when the segment can't be flattened this way (e.g. a subshell,
/// negation, command substitution, or a group of headers sharing here-document bodies).
/// </param>
public sealed record ShellCommandChainSegment(
    string Text,
    IReadOnlyList<(string Name, IReadOnlyList<string> Arguments)>? Stages);
