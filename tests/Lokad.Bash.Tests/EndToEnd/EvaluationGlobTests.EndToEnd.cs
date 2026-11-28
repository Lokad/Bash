using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationGlobTestsEndToEnd
{
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
