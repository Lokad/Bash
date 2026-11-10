using System;

namespace Lokad.Bash.Exceptions
{
    /// <summary>
    ///     Avoids failure when the destination pipe is closed by the user.
    /// </summary>
    public sealed class ClosedPipeException : Exception
    {
        public ClosedPipeException() { }
    }
}
