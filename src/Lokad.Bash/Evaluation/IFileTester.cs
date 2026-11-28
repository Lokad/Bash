using System;
using System.Threading.Tasks;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Provides filesystem queries needed during word expansion.
/// </summary>
internal interface IFileTester
{
    /// <summary>
    /// Lists the entries in the specified directory, preserving expected lookup failures.
    /// </summary>
    Task<DirectoryListingResult> ListDirectoryAsync(string path);

    /// <summary>Tests whether a path resolves to a directory, following its final symbolic link.</summary>
    Task<bool> IsDirectoryAsync(string path);
}
