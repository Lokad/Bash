using System.IO;

namespace Lokad.Bash.Tree;

/// <summary>The path exists, but its selected immutable content version can no longer be read.</summary>
/// <remarks>Hosts report this expected access failure separately from unexpected storage I/O failures.</remarks>
public sealed class FileContentUnavailableException(string path) : IOException($"{path}: selected version unavailable");
