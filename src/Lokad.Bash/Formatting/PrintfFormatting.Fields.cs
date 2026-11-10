namespace Lokad.Bash.Formatting;

public static partial class PrintfFormatting
{
    private sealed partial class Formatter
    {
        private enum FormattedNumberSign
        {
            None,
            Positive,
            Negative
        }

        private enum FormattedNumberPadding
        {
            SpaceOnly,
            ZeroAllowed
        }

        private readonly record struct FormattedNumber(
            string Text,
            string Prefix,
            FormattedNumberSign Sign,
            FormattedNumberPadding Padding);
    }
}
