using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationAggregateExpansionTestsEndToEnd
{
    public static TheoryData<string, string, string, string> AggregateFieldCases =>
        new()
        {
            { "unset values", string.Empty, @"""${values[@]}""", "count=0" },
            { "unset values", string.Empty, @"""${values[*]}""", "count=1:<>" },
            { "values=()", string.Empty, @"${values[@]}", "count=0" },
            { "values=()", string.Empty, @"${values[*]}", "count=0" },
            { "values=(); values[1]=; values[3]='a b'", string.Empty, @"""${values[@]}""", "count=2:<>:<a b>" },
            { "values=(); values[1]=; values[3]='a b'", string.Empty, @"${values[@]}", "count=2:<a>:<b>" },
            { "values=(); values[1]=; values[3]='a b'", string.Empty, @"""${values[*]}""", "count=1:< a b>" },
            { "values=(); values[1]=; values[3]='a b'", string.Empty, @"${values[*]}", "count=2:<a>:<b>" },
            { "values=(); values[1]=; values[3]='a b'", "IFS=,", @"""${values[@]}""", "count=2:<>:<a b>" },
            // Non-whitespace IFS retains the leading empty field when joining unquoted @.
            { "values=(); values[1]=; values[3]='a b'", "IFS=,", @"${values[@]}", "count=2:<>:<a b>" },
            { "values=(); values[1]=; values[3]='a b'", "IFS=,", @"""${values[*]}""", "count=1:<,a b>" },
            { "values=(); values[1]=; values[3]='a b'", "IFS=,", @"${values[*]}", "count=2:<>:<a b>" },
            { "values=(); values[1]=; values[3]='a b'", "IFS=", @"""${values[@]}""", "count=2:<>:<a b>" },
            { "values=(); values[1]=; values[3]='a b'", "IFS=", @"${values[@]}", "count=1:<a b>" },
            { "values=(); values[1]=; values[3]='a b'", "IFS=", @"""${values[*]}""", "count=1:<a b>" },
            { "values=(); values[1]=; values[3]='a b'", "IFS=", @"${values[*]}", "count=1:<a b>" },
        };

    [Theory]
    [MemberData(nameof(AggregateFieldCases))]
    public async Task AggregateFieldMatrix_PreservesSparsePresentElementsAndQuoteSemantics(
        string setup,
        string ifsSetup,
        string expansion,
        string expected)
    {
        var separatorSetup = ifsSetup.Length == 0 ? string.Empty : $" {ifsSetup};";
        var script = $@"{setup};{separatorSetup} set -- {expansion}; printf 'count=%s' ""$#""; for item in ""$@""; do printf ':<%s>' ""$item""; done; printf '\n'";

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal($"{expected}\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("values=('a' '' 'b'); IFS=; set -- ${values[@]}", "count=2:<a>:<b>\n")]
    [InlineData("values=('a' '' 'b'); IFS=; set -- ${values[*]}", "count=2:<a>:<b>\n")]
    [InlineData("set -- a '' b; IFS=; set -- $*", "count=2:<a>:<b>\n")]
    [InlineData("IFS=', '; value=' a, , b, '; set -- $value", "count=3:<a>:<>:<b>\n")]
    [InlineData("IFS=' ,'; left='a '; right=', b'; set -- $left$right", "count=2:<a>:<b>\n")]
    [InlineData("IFS=' ,'; left='a '; middle=; right=', b'; set -- $left$middle$right", "count=2:<a>:<b>\n")]
    [InlineData("value=' a b '; set -- pre${value}post", "count=4:<pre>:<a>:<b>:<post>\n")]
    [InlineData("IFS=,; value='a,b'; set -- pre,${value},post", "count=2:<pre,a>:<b,post>\n")]
    [InlineData("IFS=,; value='a,'; set -- ${value}\"\"", "count=2:<a>:<>\n")]
    [InlineData("IFS=' ,'; value=' ,a'; set -- ''${value}", "count=2:<>:<a>\n")]
    [InlineData("IFS=' ,'; left='a '; right=', b'; set -- $left\"\"$right", "count=3:<a>:<>:<b>\n")]
    [InlineData("values=(); values[1]='a b'; values[3]=; values[7]=c; set -- \"pre${values[@]}post\"", "count=3:<prea b>:<>:<cpost>\n")]
    [InlineData("values=(); values[2147483647]=last; values[1]=first; set -- \"${values[@]}\"", "count=2:<first>:<last>\n")]
    [InlineData("values='a b'; set -- \"${values[@]}\"", "count=1:<a b>\n")]
    [InlineData("values='a b'; set -- \"${values[*]}\"", "count=1:<a b>\n")]
    [InlineData("values=(); set -- \"${values[*]}\"", "count=1:<>\n")]
    [InlineData("unset values; set -- \"pre${values[@]}post\"", "count=1:<prepost>\n")]
    [InlineData("IFS=; values=('' ''); set -- pre${values[@]}post", "count=2:<pre>:<post>\n")]
    [InlineData("IFS=; values=(a ''); set -- ${values[@]}post", "count=2:<a>:<post>\n")]
    [InlineData("IFS=; values=('' a); set -- pre${values[@]}", "count=2:<pre>:<a>\n")]
    [InlineData("IFS=; values=('' ''); set -- \"pre${values[@]}post\"", "count=2:<pre>:<post>\n")]
    [InlineData("IFS=; values=('' ''); set -- pre${values[*]}post", "count=2:<pre>:<post>\n")]
    [InlineData("values=(); values[3]=last; values[1]=first; declare -n ref=values; set -- \"${ref[@]}\"", "count=2:<first>:<last>\n")]
    [InlineData("IFS=,; value=',,'; set -- ${value}", "count=2:<>:<>\n")]
    [InlineData("value='   '; set -- ${value}", "count=0\n")]
    [InlineData("value='   '; set -- \"\"${value}\"\"", "count=2:<>:<>\n")]
    [InlineData("left='a '; set -- '' b; set -- ${left}\"$@\"", "count=3:<a>:<>:<b>\n")]
    [InlineData("IFS=; values=(a '' b); set -- \"${values[*]}\"", "count=1:<ab>\n")]
    [InlineData("values=('*' '[a]'); set -- \"${values[@]}\"", "count=2:<*>:<[a]>\n")]
    [InlineData("cd /; values=('a b' c); set -- ~+/\"${values[@]}\"", "count=2:<//a b>:<c>\n")]
    [InlineData("cd /; value='a b'; set -- ~+/\"$value\"", "count=1:<//a b>\n")]
    public async Task AggregateAndScalarExpansion_PreservesFieldBoundaries(
        string setupAndExpansion,
        string expected)
    {
        var script = setupAndExpansion
            + @"; printf 'count=%s' ""$#""; for item in ""$@""; do printf ':<%s>' ""$item""; done; printf '\n'";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("IFS=' ,'", "count=2:<a>:<b>\n")]
    [InlineData("IFS=', '", "count=3:<>:<a>:<b>\n")]
    [InlineData("IFS='\t,'", "count=1:<a b>\n")]
    [InlineData("IFS=a", "count=3:<>:<>:< b>\n")]
    public async Task AggregateExpansion_UsesFirstIfsCharacterBetweenElements(
        string ifsSetup,
        string expected)
    {
        var script = $@"values=(); values[1]=; values[3]='a b'; {ifsSetup}; set -- ${{values[@]}}; printf 'count=%s' ""$#""; for item in ""$@""; do printf ':<%s>' ""$item""; done; printf '\n'";

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AssociativeAggregateExpansion_UsesPlatformOrdinalKeyOrder()
    {
        // Bash hash-table order is not the platform's deterministic key-order contract.
        var script = @"declare -A values; values[z]=last; values[a]=first; set -- ""${values[@]}""; printf 'count=%s' ""$#""; for item in ""$@""; do printf ':<%s>' ""$item""; done";

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("count=2:<first>:<last>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
