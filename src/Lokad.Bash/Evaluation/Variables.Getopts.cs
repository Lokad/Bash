using System;
using System.Buffers.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

// The published index and the current bundle can differ after an explicit seek.
// Keep only offsets because arguments may change and can have command-local storage.
internal readonly record struct GetoptsCursor(int ArgumentIndex, int BundleIndex, int ByteOffset)
{
    // A later index selection can still reuse the bundle until a scan starts.
    internal bool RestartsAtFirstArgument { get; init; }

    internal static GetoptsCursor AtArgument(int index) => new(index, 0, 0);

    internal GetoptsCursor InvalidateIndex() => this with { ArgumentIndex = 0, RestartsAtFirstArgument = false };
}

public sealed partial class Variables
{
    private GetoptsCursor _getoptsCursor;

    internal GetoptsCursor GetoptsPosition
    {
        get
        {
            var cursor = _getoptsCursor.ArgumentIndex == 0 ? SelectGetoptsIndex() : _getoptsCursor;
            return cursor.RestartsAtFirstArgument ? GetoptsCursor.AtArgument(1) : cursor;
        }
        set => _getoptsCursor = value;
    }

    internal bool GetoptsDiagnosticsEnabled =>
        !TryGetExpansionValue("OPTERR", out var value) || value.IsEmpty || ReadDecimalPrefix(value) != 0;

    // OPTARG removal in getopts unsets the name itself, including when it is a nameref.
    internal void ClearGetoptsArgument() => ClearBinding("OPTARG");

    internal void ResetGetoptsPosition(BindingName name)
    {
        if (name.CachedText == "OPTIND")
            _getoptsCursor = _getoptsCursor.InvalidateIndex();
    }

    internal void ReselectGetoptsIndex(BindingName name)
    {
        if (name.CachedText == "OPTIND")
            _getoptsCursor = SelectGetoptsIndex();
    }

    private GetoptsCursor SelectGetoptsIndex()
    {
        var index = TryGetExpansionValue("OPTIND", out var value) ? ReadDecimalPrefix(value) : 0;
        return _getoptsCursor with { ArgumentIndex = Math.Max(index, 1), RestartsAtFirstArgument = index <= 1 };
    }

    private static int ReadDecimalPrefix(ShellValue value)
    {
        var bytes = AsciiByte.TrimLeadingPosixWhitespace(value.Bytes.Span);
        if (Utf8Parser.TryParse(bytes, out long parsed, out _))
            return unchecked((int)parsed);

        // A decimal wider than the signed range saturates before conversion to an int.
        var digits = !bytes.IsEmpty && bytes[0] is (byte)'+' or (byte)'-' ? bytes[1..] : bytes;
        return !digits.IsEmpty && digits[0] is >= (byte)'0' and <= (byte)'9' && bytes[0] != (byte)'-' ? -1 : 0;
    }
}
