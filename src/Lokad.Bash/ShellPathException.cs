using System;

namespace Lokad.Bash;

/// <summary> Raised when a shell path operation cannot be completed. </summary>
public sealed class ShellPathException(string message) : InvalidOperationException(message);
