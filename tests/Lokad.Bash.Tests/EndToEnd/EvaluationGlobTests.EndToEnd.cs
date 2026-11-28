using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationGlobTestsEndToEnd
{
    [Theory]
    [InlineData("**/leaf", "<a/b/b/leaf><a/b/leaf><a/leaf><leaf>")]
    [InlineData("**//leaf", "<a/b/b/leaf><a/b/leaf><a/leaf>")]
    [InlineData("**/", "<a/><a/b/><a/b/b/><empty/>")]
    [InlineData("empty/**", "<empty/>")]
    [InlineData("a/**/**", "<a><a/b><a/b/b><a/b/b/leaf><a/b/leaf><a/leaf>")]
    [InlineData("a/**/b/**/leaf", "<a/b/b/leaf><a/b/b/leaf><a/b/leaf>")]
    [InlineData("a/**//**/", "<a/><a/b/><a/b/><a/b/b/><a/b/b/><a/b/b/>")]
    [InlineData("a//**//leaf", "<a//b/b/leaf><a//b/leaf><a//leaf>")]
    [InlineData("a/\"*\"*/leaf", "<a/**/leaf>")]
    [InlineData("$wild/leaf", "<a/b/b/leaf><a/b/leaf><a/leaf><leaf>")]
    [InlineData("\"$wild\"/leaf", "<**/leaf>")]
    [InlineData("***/leaf", "<a/leaf>")]
    public async Task GlobstarUsesActiveComponentsAndPreservesBashSpellings(string pattern, string expected)
    {
        var host = EndToEndHarness.BuildTree(["/a", "/a/b", "/a/b/b", "/empty"],
            [("/leaf", ""), ("/a/leaf", ""), ("/a/b/leaf", ""), ("/a/b/b/leaf", "")]);
        var result = await EvaluationTestHelper.RunAsync(
            "shopt -s globstar\nwild='**'\nprintf '<%s>' " + pattern, tree: host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task GlobstarSharesSuffixLookupsAndUsesReachedOptions()
    {
        await using var host = EndToEndHarness.BuildTree(["/a", "/a/b", "/a/.hidden"],
            [("/a/leaf.txt", ""), ("/a/b/leaf.txt", ""), ("/a/.hidden/leaf.txt", ""), ("/leaf.txt", "")]);
        var directories = new List<ShellPath>();
        host.BeforeListDirectory = directories.Add;
        var result = await EvaluationTestHelper.RunAsync("""
            shopt -s globstar
            GLOBIGNORE='a/b/leaf.txt'
            printf '<%s>' **/*.txt
            """, tree: host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<a/.hidden/leaf.txt><a/leaf.txt><leaf.txt>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(new[] { "/", "/a", "/a/.hidden", "/a/b" },
            directories.Select(path => path.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GlobstarUsesShellStateInFunctionsSubshellsAndFreshLaunches()
    {
        var host = EndToEndHarness.BuildTree([("/leaf", ""), ("/a/b/leaf", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            shopt -p globstar
            shopt -s globstar
            ( shopt -u globstar; printf 'child:<%s>\n' **/leaf )
            printf 'parent:<%s>\n' **/leaf
            f() { local -; shopt -u globstar; }
            f
            shopt -p globstar
            bash -O globstar -c 'printf "launch:<%s>\n" **/leaf'
            shopt -s globstar
            export BASHOPTS
            bash +O globstar -c 'printf "import:<%s>\n" **/leaf'
            """, tree: host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("shopt -u globstar\nchild:<**/leaf>\nparent:<a/b/leaf>\nparent:<leaf>\nshopt -u globstar\n"
            + "launch:<a/b/leaf>\nlaunch:<leaf>\nimport:<a/b/leaf>\nimport:<leaf>\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task NoglobSkipsPathnameLookupsAndKeepsWordAndPatternExpansion()
    {
        await using var host = EndToEndHarness.BuildTree([("/itemA.txt", "A")]);
        var listings = new List<ShellPath>();
        host.BeforeListDirectory = listings.Add;
        var result = await EvaluationTestHelper.RunAsync("""
            shopt -s failglob nullglob
            set -f
            IFS=:
            value='item[AB].txt:extra'
            printf 'split:<%s>\n' $value
            printf 'brace:<%s>\n' item{a,b}[AB]
            [[ word == w* ]] && printf 'conditional\n'
            case word in w*) printf 'case\n';; esac
            set +f
            printf 'expanded:<%s>\n' item[AB].txt
            """, tree: host);

        Assert.Equal("split:<item[AB].txt>\nsplit:<extra>\nbrace:<itema[AB]>\nbrace:<itemb[AB]>\n"
            + "conditional\ncase\nexpanded:<itemA.txt>\n", result.StdOut);
        Assert.Equal(ShellPath.Root, Assert.Single(listings));
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task NoglobUsesLiteralRedirectionTargetsAndArrayFields()
    {
        await using var host = EndToEndHarness.BuildTree([("/itemA.txt", "initial\n")]);
        var result = await EvaluationTestHelper.RunAsync("""
            set -o noglob
            printf 'literal\n' >item[AB].txt
            read -r value <item[AB].txt
            printf 'read:<%s>\n' "$value"
            declare -a a=( item[AB].txt )
            b=( item[AB].txt )
            printf 'arrays:<%s>\n' "${a[@]}" "${b[@]}"
            set +o noglob
            printf 'expanded\n' >item[AB].txt
            """, tree: host);

        Assert.Equal("read:<literal>\narrays:<item[AB].txt>\narrays:<item[AB].txt>\n", result.StdOut);
        Assert.Equal("literal\n", host.ReadFile("/item[AB].txt"));
        Assert.Equal("expanded\n", host.ReadFile("/itemA.txt"));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task NoglobRestoresTheFunctionSnapshotAndReportsTheReachedState()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local -
                shopt -os noglob
                local -
                printf 'local:%s:%s:%s\n' "$-" "${-}" "$SHELLOPTS"
                [[ -o noglob ]] && test -o noglob && shopt -oq noglob && printf 'active\n'
                printf 'capture:%s\n' "$(printf '%s' "$-")"
            }
            f
            printf 'outer:%s:%s\n' "$-" "$SHELLOPTS"
            [[ -o noglob ]] || printf 'restored\n'
            """);

        Assert.Equal("local:fB:fB:braceexpand:noglob:pipefail\nactive\ncapture:fB\n"
            + "outer:B:braceexpand:pipefail\nrestored\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GlobIgnoreFiltersFinalPathSpellingsBeforeApplyingNoMatchPolicy()
    {
        var host = EndToEndHarness.BuildTree(["/dir", "/dir/sub"],
            [("/dir/sub/a.txt", ""), ("/dir/visible.txt", ""), ("/dir/.hidden.txt", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            GLOBIGNORE='dir/*'
            printf 'fallback:<%s>\n' dir/*/*.txt
            shopt -s nullglob
            set -- dir/*/*.txt
            printf 'null:%s\n' "$#"
            GLOBIGNORE='dir/sub/a.txt'
            printf 'prefix:<%s>\n' dir//sub/*.txt
            shopt -s failglob
            printf wrong dir/*/*.txt; printf skipped
            printf 'status:%s\n' "$?"
            """, tree: host);

        Assert.Equal("fallback:<dir/*/*.txt>\nnull:0\nprefix:<dir//sub/a.txt>\nstatus:1\n", result.StdOut);
        Assert.Equal("line 9: no match: dir/*/*.txt\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a/*/leaf.txt", "<axleaf.txt><a/leaf.txt><a/xleaf.txt><a//xleaf.txt><a/b/c/leaf.txt>")]
    [InlineData("a/**/leaf.txt", "<axleaf.txt><a/leaf.txt><a/xleaf.txt><a//leaf.txt><a//xleaf.txt><a/b/c/leaf.txt>")]
    [InlineData("a/*", "<axleaf.txt>")]
    [InlineData("a/**", "<axleaf.txt><a//leaf.txt><a//xleaf.txt>")]
    [InlineData("a?leaf.txt", "<a/leaf.txt><a/xleaf.txt><a//leaf.txt><a//xleaf.txt><a/b/leaf.txt><a/b/c/leaf.txt>")]
    [InlineData("a[[:punct:]]leaf.txt:a/xleaf.txt", "<axleaf.txt><a//leaf.txt><a//xleaf.txt><a/b/leaf.txt><a/b/c/leaf.txt>")]
    [InlineData("a[/x]leaf.txt", "<a/xleaf.txt><a//leaf.txt><a//xleaf.txt><a/b/leaf.txt><a/b/c/leaf.txt>")]
    [InlineData("a[x/]leaf.txt", "<axleaf.txt><a/leaf.txt><a/xleaf.txt><a//leaf.txt><a//xleaf.txt><a/b/leaf.txt><a/b/c/leaf.txt>")]
    [InlineData(@"a/*\/leaf.txt", "<axleaf.txt><a/leaf.txt><a/xleaf.txt><a//leaf.txt><a//xleaf.txt><a/b/leaf.txt><a/b/c/leaf.txt>")]
    public async Task GlobIgnoreUsesBashSeparatorRulesForPublishedPaths(string ignore, string expected)
    {
        var host = EndToEndHarness.BuildTree([("/axleaf.txt", ""), ("/a/leaf.txt", ""),
            ("/a/xleaf.txt", ""), ("/a/b/leaf.txt", ""), ("/a/b/c/leaf.txt", "")]);
        var result = await EvaluationTestHelper.RunAsync($"""
            shopt -s nullglob
            GLOBIGNORE='{ignore}'
            for path in *.txt a/*.txt a//*.txt a/*/*.txt a/*/*/*.txt; do printf '<%s>' "$path"; done
            """, tree: host);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("'left:middle:right':drop", "drop")]
    [InlineData("'left:middle:drop", "")]
    [InlineData("'[left':middle", "middle")]
    [InlineData("'name*':drop", "'name1'|drop")]
    [InlineData("\"left:middle:right\":drop", "drop")]
    [InlineData("`left:middle:right`:drop", "drop")]
    [InlineData("\"left:$(printf \"middle:right\"):right\":drop", "drop")]
    [InlineData("$(printf left:middle:right):drop", "drop")]
    [InlineData("$(echo left # ):middle:right):drop", "")]
    [InlineData("${a[one}middle]:-two:right}:drop", "drop")]
    [InlineData("${a:-[one}:middle:right]:drop", "middle|drop")]
    [InlineData("$(echo ${a:-some)text}:middle:right):drop", "middle|drop")]
    [InlineData(">(left:middle:right):drop", "drop")]
    public async Task GlobIgnoreSplitsOnlyAtUngroupedColons(string ignore, string excluded)
    {
        string[] names = ["'left", "'name1'", "[left", "drop", "keep", "middle", "name1", "right'"];
        var host = EndToEndHarness.BuildTree(names.Select(name => ("/" + name, "")));
        var result = await EvaluationTestHelper.RunAsync(
            $"GLOBIGNORE={ShellArgumentEscaping.EscapeArgument(ignore)}\nprintf '<%s>' *", tree: host);

        var expected = string.Concat(names.Except(excluded.Split('|'), StringComparer.Ordinal).Select(name => $"<{name}>"));
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("$(case a in a) printf \"left:middle:right\";; esac)", "<middle>", true)]
    [InlineData("$(cat <<'BODY'\n) \"left:middle:right\"\nBODY\n)", "<middle>", true)]
    [InlineData("$(printf a;# ) \"left:middle:right\"\n)", "<middle>", true)]
    [InlineData("$(case a in a) printf \"left:middle:right\"", "<drop><middle>", false)]
    public async Task GlobIgnoreUsesTheCommandReaderForDoubleQuotedBoundaries(string nested, string expected, bool complete)
    {
        var value = ShellArgumentEscaping.EscapeArgument($"\"left:{nested}:right\":drop");
        var host = EndToEndHarness.BuildTree([("/drop", ""), ("/middle", "")]);
        var result = await EvaluationTestHelper.RunAsync($"""
            GLOBIGNORE={value}
            printf '<%s>' *
            ( GLOBIGNORE=middle; printf 'child:<%s>' * )
            printf '<%s>' *
            """, tree: host);

        Assert.Equal(expected + "child:<drop>" + expected, result.StdOut);
        if (complete)
            Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GlobIgnoreReportsOnlyReachedChangesBeforeTheNextCommand()
    {
        var value = ShellArgumentEscaping.EscapeArgument("\"left:$(case a in a) printf \"left:middle:right\":drop");
        var result = await EvaluationTestHelper.RunAsync($"""
            bad={value}
            exec 2>&1
            GLOBIGNORE=$bad
            printf 'first:%s\n' "$?"
            GLOBIGNORE=$bad
            printf 'same:%s\n' "$?"
            GLOBIGNORE=ordinary
            GLOBIGNORE=$bad
            printf 'restored:%s\n' "$?"
            """);

        Assert.Equal("command substitution: line 5: unexpected EOF while looking for matching `)'\nfirst:0\nsame:0\n"
            + "command substitution: line 10: unexpected EOF while looking for matching `)'\nrestored:0\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("GLOBIGNORE=$bad", false)]
    [InlineData("GLOBIGNORE=$bad printf ''", false)]
    [InlineData("declare GLOBIGNORE=$bad", true)]
    [InlineData("read -r GLOBIGNORE", true)]
    public async Task GlobIgnoreDiagnosticUsesTheAssignmentPhaseDescriptor(string assignment, bool redirected)
    {
        const string Pattern = "\"left:$(case a in a) printf \"left:middle:right\":drop";
        var value = ShellArgumentEscaping.EscapeArgument(Pattern);
        var input = assignment.StartsWith("read", StringComparison.Ordinal) ? " <<'VALUE'\n" + Pattern + "\nVALUE" : "";
        await using var host = new TestHost();
        var result = await host.RunAsync($"bad={value}\n{assignment} 2>diagnostic{input}\nprintf 'status:%s' \"$?\"\n",
            CancellationToken.None);

        const string Message = "command substitution: line 4: unexpected EOF while looking for matching `)'\n";
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("status:0", host.StandardOutput.TextContent);
        Assert.Equal(redirected ? "" : Message, host.StandardError.TextContent);
        Assert.Equal(redirected ? Message : "", host.ReadFile("/diagnostic"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("GLOBIGNORE=$bad || printf fallback")]
    [InlineData("GLOBIGNORE=$bad printf builtin")]
    public async Task GlobIgnoreSyntaxFailureExitsTheErrexitShellBeforeTheCommandContinues(string command)
    {
        var value = ShellArgumentEscaping.EscapeArgument("\"left:$(case a in a) printf \"left:middle:right\":drop");
        var result = await EvaluationTestHelper.RunAsync($"set -e\nbad={value}\n{command}\nprintf tail\n");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("command substitution: line 5: unexpected EOF while looking for matching `)'\n", result.StdErr);
    }

    [Fact]
    public async Task GlobIgnoreFatalPrefixLeavesTheRedirectionUnopened()
    {
        var value = ShellArgumentEscaping.EscapeArgument("\"left:$(case a in a) printf \"left:middle:right\":drop");
        await using var host = new TestHost();
        var result = await host.RunAsync($"bad={value}\nset -e\nGLOBIGNORE=$bad printf side 2>diagnostic\nprintf tail\n",
            CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("command substitution: line 5: unexpected EOF while looking for matching `)'\n", host.StandardError.TextContent);
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/diagnostic"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task GlobIgnorePrefixReportsARestoredValueBeforeTheNextPrefixExpands()
    {
        var value = ShellArgumentEscaping.EscapeArgument("\"left:$(case a in a) printf \"left:middle:right\":drop");
        var result = await EvaluationTestHelper.RunAsync($"""
            bad={value}
            exec 2>&1
            GLOBIGNORE=$bad
            GLOBIGNORE=ordinary GLOBIGNORE=$bad later=$(printf 'next\n' >&2) printf 'body\n'
            """);

        Assert.Equal("command substitution: line 5: unexpected EOF while looking for matching `)'\n"
            + "command substitution: line 6: unexpected EOF while looking for matching `)'\nnext\nbody\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GlobIgnoreUnexpectedTokenDiagnosticKeepsTheCommandBodySpelling()
    {
        var value = ShellArgumentEscaping.EscapeArgument("\"prefix:$(printf left &&):right\":drop");
        var result = await EvaluationTestHelper.RunAsync($"GLOBIGNORE={value}\nprintf 'status:%s' \"$?\"\n");

        Assert.Equal("status:0", result.StdOut);
        Assert.Equal("command substitution: line 2: syntax error near unexpected token `)'\n"
            + "command substitution: line 2: `printf left &&):right\":drop'\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ShellWordPatternsCanMatchSlashWithGlobIgnoreSet()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            GLOBIGNORE='a?b'
            value=a/b
            case "$value" in a?b) printf 'case\n';; esac
            [[ $value == a*b ]] && printf 'conditional\n'
            printf 'trim:<%s>\n' "${value#a?}"
            """);

        Assert.Equal("case\nconditional\ntrim:<b>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GlobIgnoreFiltersCompletedDotPathsAndKeepsTheirSpelling()
    {
        var host = EndToEndHarness.BuildTree(["/dir/z", "/dir/a", "/dir/m"],
            [("/dir/a/leaf", ""), ("/dir/m/leaf", ""), ("/dir/z/leaf", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            GLOBIGNORE='dir/a:dir/a/./:dir/m/../:dir//m/leaf'
            shopt -s nullglob
            printf 'dot:<%s>\n' dir/*/./
            set -- dir/*/.
            printf 'dot-count:%s\n' "$#"
            printf 'parent:<%s>\n' dir/*/../
            printf 'leaf:<%s>\n' dir//*/leaf
            """, tree: host);

        Assert.Equal("dot:<dir/m/./>\ndot:<dir/z/./>\ndot-count:0\n"
            + "parent:<dir/a/../>\nparent:<dir/z/../>\nleaf:<dir//a/leaf>\nleaf:<dir//z/leaf>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GlobIgnoreFollowsFunctionPrefixesAndEmptyLocalAssignments()
    {
        var host = EndToEndHarness.BuildTree([("/.hidden", ""), ("/skip", ""), ("/visible", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            GLOBIGNORE=skip
            shopt -u dotglob
            f() {
                printf 'prefix:<%s>\n' *
                local GLOBIGNORE=''
                printf 'empty:<%s>\n' *
            }
            GLOBIGNORE=visible f
            printf 'restored:<%s>\n' *
            unset GLOBIGNORE
            printf 'unset:<%s>\n' *
            """, tree: host);

        Assert.Equal("prefix:<.hidden>\nprefix:<skip>\nempty:<.hidden>\nempty:<skip>\nempty:<visible>\n"
            + "restored:<.hidden>\nrestored:<visible>\nunset:<skip>\nunset:<visible>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GlobIgnoreRetainsTheSelectedNamerefValueUntilTheBindingChanges()
    {
        var host = EndToEndHarness.BuildTree([("/.hidden", ""), ("/skip", ""), ("/visible", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            pattern=skip
            declare -n GLOBIGNORE=pattern
            pattern=visible
            printf 'cached:<%s>\n' *
            unset -n GLOBIGNORE
            printf 'unset:<%s>\n' *
            """, tree: host);

        Assert.Equal("cached:<.hidden>\ncached:<visible>\nunset:<skip>\nunset:<visible>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GlobIgnoreArrayInputLeavesTheFilterUntilAnAssignmentOrUnset()
    {
        var host = EndToEndHarness.BuildTree([("/.hidden", ""), ("/skip", ""), ("/visible", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            read -r GLOBIGNORE <<'ROW'
            skip
            ROW
            mapfile -t GLOBIGNORE <<'ROWS'
            visible
            other
            ROWS
            printf 'mapfile:<%s>\n' *
            GLOBIGNORE[1]=replacement
            printf 'assignment:<%s>\n' *
            unset 'GLOBIGNORE[0]'
            printf 'unset:<%s>\n' *
            read -a GLOBIGNORE <<'FIELDS'
            skip other
            FIELDS
            printf 'read-array:<%s>\n' *
            """, tree: host);

        Assert.Equal("mapfile:<.hidden>\nmapfile:<visible>\nassignment:<.hidden>\nassignment:<skip>\n"
            + "unset:<skip>\nunset:<visible>\nread-array:<skip>\nread-array:<visible>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExplicitDotWildcardsUseSuccessfulListingsAndKeepLookupSpelling()
    {
        var host = EndToEndHarness.BuildTree(["/dir", "/dir/.hidden", "/empty"],
            [("/visible", ""), ("/dir/visible", ""), ("/dir/.hidden/visible", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            printf 'default:<%s>\n' dir/.*
            shopt -u globskipdots
            printf 'nested:<%s>\n' dir/"."*/visible
            printf 'found:<%s>\n' empty/.* missing/.*
            shopt -s dotglob
            printf 'bracket:<%s>\n' dir/[.]*
            """, tree: host);

        Assert.Equal("default:<dir/.hidden>\n"
            + "nested:<dir/../visible>\nnested:<dir/./visible>\nnested:<dir/.hidden/visible>\n"
            + "found:<empty/.>\nfound:<empty/..>\nfound:<missing/.*>\nbracket:<dir/.hidden>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DotEntryCanMakeANamedRedirectionAmbiguousWithoutOpeningAFile()
    {
        await using var host = EndToEndHarness.BuildTree(["/empty"], [
            ("/empty/.visible", "original"),
            ("/unit", "shopt -u globskipdots\nprintf wrong >empty/.*\nprintf 'status:%s\\n' \"$?\"\n")
        ]);

        var result = await host.RunAsync("bash ./unit", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("status:1\n", host.StandardOutput.TextContent);
        Assert.Equal("./unit: line 2: empty/.*: ambiguous redirect\n", host.StandardError.TextContent);
        Assert.Equal("original", host.ReadFile("/empty/.visible"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task NocaseglobFoldsWildcardComponentsAndKeepsOriginalDirectorySpellings()
    {
        var host = EndToEndHarness.BuildTree(["/Dir"], [("/Dir/UPPER.TXT", ""), ("/Dir/lower.txt", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            shopt -s nocaseglob
            printf '<%s>\n' d*/[u-z]*.txt d*/lower.txt
            [[ A == a ]]; printf 'text:%s\n' "$?"
            """, tree: host);

        Assert.Equal("<Dir/UPPER.TXT>\n<Dir/lower.txt>\ntext:1\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task NullglobDropsFieldsBeforeCommandSelectionAndArrayPublication()
    {
        var host = EndToEndHarness.BuildTree([("/one.keep", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            shopt -s nullglob
            *.missing printf '<%s>\n' '*.missing' *.keep *.missing
            scalar=*.missing
            declare -a array=( *.missing *.keep *.missing )
            printf 'scalar:%s array:%s:%s\n' "$scalar" "${#array[@]}" "${array[0]}"
            """, tree: host);

        Assert.Equal("<*.missing>\n<one.keep>\nscalar:*.missing array:1:one.keep\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DotglobPersistsAfterLocalOptionsAndMatchesHiddenComponents()
    {
        var host = EndToEndHarness.BuildTree(["/dir", "/.dir"],
            [("/dir/visible", ""), ("/dir/.leaf", ""), ("/.dir/.leaf", "")]);
        var result = await EvaluationTestHelper.RunAsync("""
            printf 'before:%s\n' */*
            enable() { local -; shopt -s dotglob; }
            enable
            printf 'after:%s\n' */*
            """, tree: host);

        Assert.Equal("before:dir/visible\nafter:.dir/.leaf\nafter:dir/.leaf\nafter:dir/visible\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("printf '<%s>' *.missing || printf skipped")]
    [InlineData("if printf '<%s>' *.missing; then printf skipped; else printf skipped; fi")]
    [InlineData(">*.missing; printf skipped")]
    public async Task FailglobDiscardsTheCommandUnitAndPublishesItsStatus(string command)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "shopt -s failglob nullglob\n" + command
            + "\nprintf 'end:%s:%s:%s\\n' \"$?\" \"${PIPESTATUS[*]}\" \"$_\"\n");

        var result = await host.RunAsync("bash ./unit", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("end:1:1:nullglob\n", host.StandardOutput.TextContent);
        Assert.Equal("./unit: line 2: no match: *.missing\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/*.missing"));
    }

    [Theory]
    [InlineData("*.missing", "*.missing", 0)]
    [InlineData("${patterns[i++]}", "second", 2)]
    public async Task NullglobRedirectionUsesTheReachedDiagnosticSpelling(string expression, string spelling, int index)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "shopt -s nullglob\npatterns=('*.missing' second)\ni=0\n: >" + expression
            + "\nprintf 'end:%s:%s\\n' \"$?\" \"$i\"\n");

        var result = await host.RunAsync("bash ./unit", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"end:1:{index}\n", host.StandardOutput.TextContent);
        Assert.Equal($"./unit: line 4: {spelling}: ambiguous redirect\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/second"));
    }

    [Theory]
    [InlineData("?", "<7><é><😀><😁>")]
    [InlineData("??", "<😀x><😁x>")]
    [InlineData("????", "<????>")]
    [InlineData("[😀]", "<😀>")]
    [InlineData("[!a]", "<7><é><😀><😁>")]
    [InlineData("[😀-😂]", "<😀><😁>")]
    [InlineData("[😀]*", "<😀><😀[x><😀x>")]
    [InlineData("[😀[:digit:]]", "<7><😀>")]
    [InlineData("[[.😀.]]", "<😀>")]
    [InlineData("[[=😀=]]", "<😀>")]
    [InlineData("😀'['?", "<😀[x>")]
    [InlineData("[😀*", "<[😀x>")]
    public async Task Evaluation_GlobExpansion_CountsSupplementaryCharactersOnce(string pattern, string expected)
    {
        var tree = EndToEndHarness.BuildTree([
            ("/7", ""), ("/é", ""), ("/😀", ""), ("/😁", ""),
            ("/😀x", ""), ("/😁x", ""), ("/😀[x", ""), ("/[😀x", "")
        ]);
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' " + pattern, tree: tree);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_SelectsSupplementaryDirectoryForFileEffects()
    {
        await using var host = EndToEndHarness.BuildTree(
            ["/dir", "/dir/😀", "/dir/😀x"],
            [("/dir/😀/é", "initial"), ("/dir/😀x/é", "initial")]);
        var result = await host.RunAsync(
            "for file in dir/?/?; do printf selected >\"$file\"; done; printf tail",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("selected", host.ReadFile("/dir/😀/é"));
        Assert.Equal("initial", host.ReadFile("/dir/😀x/é"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_KeepsOrderingWithinEachWord()
    {
        await using var host = EndToEndHarness.BuildTree([("/\U00010000", ""), ("/\uE000", "")]);
        var result = await host.RunAsync("printf '<%s>' '\U00010000'* *", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("3CF09080803E3CEE80803E3CF09080803E",
            Convert.ToHexString(host.StandardOutput.Content.ToArray()));
    }

    [Theory]
    [InlineData("$1", "\uE000")]
    [InlineData("$2", "\U00010000")]
    public async Task Evaluation_GlobExpansion_UsesPathOrderForFileSelection(string operand, string expected)
    {
        await using var host = EndToEndHarness.BuildTree([("/\U00010000", ""), ("/\uE000", "")]);
        var result = await host.RunAsync(
            $"set -- *; printf '%s' \"{operand}\" >chosen; printf tail", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(expected, host.ReadFile("/chosen"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("*/", "<dir/><empty/>")]
    [InlineData("*//", "<dir/><empty/>")]
    [InlineData("dir'//'*", "<dir//file><dir//sub>")]
    [InlineData("dir///sub//l*", "<dir///sub//leaf>")]
    [InlineData("dir//*/", "<dir//sub/>")]
    [InlineData("dir/*//leaf", "<dir/sub/leaf>")]
    [InlineData("plain*/", "<plain*/>")]
    [InlineData("dir//*//missing", "<dir//*//missing>")]
    public async Task Evaluation_GlobExpansion_PreservesPathSpellingAndDirectorySelection(
        string pattern, string expected)
    {
        var host = EndToEndHarness.BuildTree(
            ["/dir", "/dir/sub", "/empty"],
            [("/dir/file", ""), ("/dir/sub/leaf", ""), ("/plain", "")]);
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' " + pattern, tree: host);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_UsesDirectoryMatchesInRedirectedPaths()
    {
        await using var host = EndToEndHarness.BuildTree(["/dir", "/empty"], [("/plain", "")]);
        var result = await host.RunAsync(
            "for item in */; do printf %s \"$item\" >\"${item}chosen\"; done; printf tail",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("dir/", host.ReadFile("/dir/chosen"));
        Assert.Equal("empty/", host.ReadFile("/empty/chosen"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/plainchosen"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_ExpandsUnquotedNamerefArrayElementsIndependently()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/a.txt", "a"),
            ("/b.txt", "b"),
            ("/note.md", "n")
        ]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; patterns=('*.txt'); declare -n reference=patterns; "
            + "printf '[%s]\\n' ${reference[@]}; printf '<%s>\\n' \"${reference[@]}\"",
            tree: tree);

        Assert.Equal("[a.txt]\n[b.txt]\n<*.txt>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_QuotedClosingBracketRemainsBracketMember()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/]", "closing"),
            ("/a", "a"),
            ("/b", "b"),
            ("/c", "c")
        ]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; printf '<%s>\\n' [a']'b]",
            tree: tree);

        Assert.Equal("<]>\n<a>\n<b>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_TreatsInitialClosingBracketAsMember()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/]", "closing"),
            ("/a", "a"),
            ("/b", "b")
        ]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; printf '<%s>\\n' []a] [!]]",
            tree: tree);

        Assert.Equal("<]>\n<a>\n<a>\n<b>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_TreatsConditionalPrefixAsBracketExpression()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/[", "opening"),
            ("/a", "a")
        ]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; printf '<%s>\\n' [[]",
            tree: tree);

        Assert.Equal("<[>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterRemoval_TreatsInitialClosingBracketAsMember()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "value=taila; printf '<%s>\\n' \"${value%[]a]}\"");

        Assert.Equal("<tail>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_ValidatesFixedSuffixAfterWildcardDirectory()
    {
        var tree = EndToEndHarness.BuildTree(
            ["/d1", "/d2"],
            [("/d1/found", "found")]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; printf '<%s>\\n' d*/missing d*/found",
            tree: tree);

        Assert.Equal("<d*/missing>\n<d1/found>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("leaf", "<d1/leaf>")]
    [InlineData("'item[ab]'", "<d1/item[ab]>")]
    [InlineData("item[ab]", "<d1/itema><d1/itemb>")]
    [InlineData("open[", "<d1/open[>")]
    [InlineData("😀é", "<d1/😀é>")]
    public async Task Evaluation_GlobExpansion_DistinguishesLiteralAndActiveSuffixes(string suffix, string expected)
    {
        var tree = EndToEndHarness.BuildTree([
            ("/d1/leaf", ""), ("/d2/Leaf", ""), ("/d2/leaflet", ""),
            ("/d1/item[ab]", ""), ("/d1/itemb", ""), ("/d1/itema", ""),
            ("/d1/open[", ""), ("/d1/open[a", ""),
            ("/d1/😀é", ""), ("/d1/😁é", ""), ("/d1/😀e\u0301", "")
        ]);
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' d*/" + suffix, tree: tree);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_RequestsDirectoryListing()
    {
        // This test verifies that unquoted globs expand against directory entries.
        // With /a.txt and /b.txt present, "echo *.txt" must list both names.
        // If glob expansion is skipped, STDOUT would be "*.txt".
        var tree = EndToEndHarness.BuildTree([
            ("/a.txt", "a"),
            ("/b.txt", "b"),
            ("/note.md", "n")
        ]);
        var script = @"cd /; echo *.txt";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        var parts = result.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);
        Assert.Contains("a.txt", parts);
        Assert.Contains("b.txt", parts);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_RecursesSegments()
    {
        // This test verifies that glob expansion can match across path segments.
        // With /a/b/c and /a/d/c present, "echo a/*/c" must list both paths.
        // If segment recursion is not supported, STDOUT would be "a/*/c".
        var tree = EndToEndHarness.BuildTree(
            ["/a", "/a/b", "/a/d"],
            [
                ("/a/b/c", "x"),
                ("/a/d/c", "y")
            ]);
        var script = @"cd /; echo a/*/c";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        var parts = result.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);
        Assert.Contains("a/b/c", parts);
        Assert.Contains("a/d/c", parts);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
