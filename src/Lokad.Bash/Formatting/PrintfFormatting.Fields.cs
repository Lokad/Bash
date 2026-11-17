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

        /// <summary>Separates exact digits from arbitrarily long, budgeted runs of zero padding.</summary>
        private readonly record struct NumberText(string BeforeZeros, int Zeros, string AfterZeros)
        {
            public long Length => (long)BeforeZeros.Length + Zeros + AfterZeros.Length;
        }

        private readonly record struct FormattedNumber(
            NumberText Text,
            string Prefix,
            FormattedNumberSign Sign,
            FormattedNumberPadding Padding);
    }
}
