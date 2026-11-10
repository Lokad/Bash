using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Lokad.Bash.Files;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Provides filesystem queries needed during word expansion.
/// </summary>
internal interface IFileTester
{
    /// <summary>
    /// Lists the entries in the specified directory.
    /// </summary>
    Task<IReadOnlyList<DirectoryEntry>> ListDirectoryAsync(string path);

    /// <summary>Tests whether a path resolves to a directory, following its final symbolic link.</summary>
    Task<bool> IsDirectoryAsync(string path);
}
