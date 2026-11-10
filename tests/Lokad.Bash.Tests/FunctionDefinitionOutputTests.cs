using Lokad.Bash.Memory;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class FunctionDefinitionOutputTests
{
    [Theory]
    [InlineData("declare -f")]
    [InlineData("declare -F")]
    [InlineData("declare")]
    public async Task DefinitionListingsStopWhenTheOutputCloses(string command)
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse("a(){ :; }; b(){ :; }; " + command));
        var host = new MockFileSystem { AppendRemainsOpen = false };

        await new Engine().RunAsync(evaluation, host);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(1, host.AppendCallCount);
    }

    [Fact]
    public void PortableTimingRemainsSyntaxWhenDefinitionsArePrinted()
    {
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ time -p :; }").Commands));
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(function, output));
        Assert.Equal("f () \n{ \n    time -p :\n}\n", Utf8Text.Decode(output.ToMemory()));
        var reparsed = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(output.ToMemory()).Commands));
        var timed = Assert.IsType<TimedCommand>(Assert.IsType<GroupCommand>(reparsed.Body).Body);
        Assert.True(timed.Portable);
    }

    [Theory]
    [InlineData("a=b")]
    [InlineData("a[1]=x")]
    public async Task DefinitionOutput_KeepsAssignmentLikeNamesReparsable(string name)
    {
        // Bash omits the required keyword here; preserve executable syntax instead of that printed form.
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("function " + name + " { printf value; }").Commands));
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(function, output));
        var printed = Utf8Text.Decode(output.ToMemory());
        Assert.Equal("function " + name + " () \n{ \n    printf value\n}\n", printed);
        var reparsed = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(printed).Commands));
        Assert.Equal(function.Name, reparsed.Name);

        var result = await EndToEndHarness.RunScriptAsync(printed + "'" + name + "'",
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("value", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public void LongListsWithBodies_RenderWithoutRecursiveTreeTraversal()
    {
        var source = "f(){ " + string.Concat(Enumerable.Repeat(":; ", 10000)) + "cat <<END; }\nbody\nEND\n";
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(source).Commands));
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(function, output));
        Assert.EndsWith("    cat <<END\nbody\nEND\n\n}\n", Utf8Text.Decode(output.ToMemory()));
    }

    // Git Bash 5.2.37: direct invocation, exported child and replayed inspection keep the same expansion boundaries.
    [Theory]
    [InlineData("\"$x\"\"y\"", "<oney>")]
    [InlineData("\"$\"\"x\"", "<$x>")]
    [InlineData("\"$\"\"{x}\"", "<${x}>")]
    [InlineData("\"$\"\"(printf injected >&2)\"", "<$(printf injected >&2)>")]
    [InlineData("\"$\"\"((1+2))\"", "<$((1+2))>")]
    [InlineData("\"$\"\"$(printf suffix)\"", "<$suffix>")]
    [InlineData("\"$@\"\"suffix\"", "<a><b csuffix>")]
    [InlineData("\"$x\"\\$\"$x\"", "<one$one>")]
    [InlineData("`(printf hi)`", "<hi>")]
    [InlineData("\"`(printf hi)`\"", "<hi>")]
    [InlineData("`printf hi # comment`", "<hi>")]
    [InlineData("\"`printf hi # comment`\"", "<hi>")]
    public async Task DefinitionRoundTripsPreserveExpansionBoundaries(string word, string expected)
    {
        var source = "x=one; xy=wrong; export x xy; f(){ printf '<%s>' " + word + "; }; "
            + "f a 'b c'; export -f f; bash -c \"f a 'b c'\"; eval \"$(declare -f f)\"; f a 'b c'";

        var result = await EndToEndHarness.RunScriptAsync(source, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected + expected + expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    // Git Bash 5.2.37 output, except retained CR, equivalent quoting/substitution delimiters and flat elif chains.
    [Theory]
    // Archived word kinds use valid function syntax; redundant literal-dollar quote boundaries coalesce.
    [InlineData("f(){ printf '%s\\n' plain'single'\"double\\\"\\\\\"\\$\"\\$\"$(printf one)\"$(printf two)\"; cat <<'END'\nbody\nEND\n}", "f () \n{ \n    printf '%s\\n' plain'single'\"double\\\"\\\\\\$\\$\"$(printf one\n)\"$(printf two\n)\";\n    cat <<'END'\nbody\nEND\n\n}\n")]
    [InlineData("f(){ :;\n}", "f () \n{ \n    :\n}\n")]
    [InlineData("f(){ printf '%s' `((n=1)); printf hi`; }", "f () \n{ \n    printf '%s' $( ((n=1)); printf hi\n)\n}\n")]
    [InlineData("f(){ printf '%s' `\\\n(printf hi)`; }", "f () \n{ \n    printf '%s' $( \\\n(printf hi)\n)\n}\n")]
    [InlineData("f(){ TIMEFORMAT=''; time printf x; }", "f () \n{ \n    TIMEFORMAT='';\n    time printf x\n}\n")]
    [InlineData("f(){ printf a; printf b; true && echo c || echo d;\n}", "f () \n{ \n    printf a;\n    printf b;\n    true && echo c || echo d\n}\n")]
    [InlineData("f(){ { echo x; echo y; };\n}", "f () \n{ \n    { \n        echo x;\n        echo y\n    }\n}\n")]
    [InlineData("f(){ (echo x; echo y);\n}", "f () \n{ \n    ( echo x;\n    echo y )\n}\n")]
    [InlineData("f(){ if true; then echo yes; elif false; then :; else echo no; fi;\n}", "f () \n{ \n    if true; then\n        echo yes;\n    elif false; then\n        :;\n    else\n        echo no;\n    fi\n}\n")]
    [InlineData("f(){ while false; do echo x; done; until true; do :; done;\n}", "f () \n{ \n    while false; do\n        echo x;\n    done;\n    until true; do\n        :;\n    done\n}\n")]
    [InlineData("f(){ for x in a 'b c' \"$@\"; do printf '%s\\n' \"$x\"; done;\n}", "f () \n{ \n    for x in a 'b c' \"$@\";\n    do\n        printf '%s\\n' \"$x\";\n    done\n}\n")]
    [InlineData("f(){ for x in; do :; done;\n}", "f () \n{ \n    for x in ;\n    do\n        :;\n    done\n}\n")]
    [InlineData("f(){ for ((i=0;i<2;i++)); do ((n+=i)); done;\n}", "f () \n{ \n    for ((i=0; i<2; i++))\n    do\n        ((n+=i));\n    done\n}\n")]
    [InlineData("f(){ echo x | cat; echo y |& cat; ! false | true;\n}", "f () \n{ \n    echo x | cat;\n    echo y 2>&1 | cat;\n    ! false | true\n}\n")]
    [InlineData("f(){ case \"$1\" in a|'b c') echo yes;; *) :;; esac;\n}", "f () \n{ \n    case \"$1\" in \n        a | 'b c')\n            echo yes\n        ;;\n        *)\n            :\n        ;;\n    esac\n}\n")]
    [InlineData("f(){ [[ $x == a* && -n \"$y\" ]];\n}", "f () \n{ \n    [[ $x == a* && -n \"$y\" ]]\n}\n")]
    [InlineData("f(){ ((x+=1));\n}", "f () \n{ \n    ((x+=1))\n}\n")]
    [InlineData("f(){ g(){ printf '%s\\n' \"$1\"; }; g x;\n}", "f () \n{ \n    function g () \n    { \n        printf '%s\\n' \"$1\"\n    };\n    g x\n}\n")]
    [InlineData("f(){ echo x 2>err >out; cat <in; echo y >&2; true 3>&-;\n}", "f () \n{ \n    echo x 2> err > out;\n    cat < in;\n    echo y 1>&2;\n    true 3>&-\n}\n")]
    [InlineData("f(){ x='a b' y+=c printf '%s\\n' \"$x\"; declare -a arr=([2]='a b' tail); arr+=(one);\n}", "f () \n{ \n    x='a b' y+=c printf '%s\\n' \"$x\";\n    declare -a arr=([2]='a b' tail);\n    arr+=(one)\n}\n")]
    [InlineData("f(){ printf '%s\\n' 'a b' \"$x\" \"a\\$x\" \"${x:-a b}\" a\\ b \"$(printf x)\";\n}", "f () \n{ \n    printf '%s\\n' 'a b' \"$x\" \"a\\$x\" \"${x:-a b}\" a\\ b \"$(printf x\n)\"\n}\n")]
    [InlineData("f(){ cat <<'END'\n$x\nEND\n\n}", "f () \n{ \n    cat <<'END'\n$x\nEND\n\n}\n")]
    [InlineData("f(){ cat <<END\n$x\nEND\n\n}", "f () \n{ \n    cat <<END\n$x\nEND\n\n}\n")]
    [InlineData("f(){ cat <<ONE | cat <<'TWO'\nfirst\nONE\nsecond\nTWO\n\n}", "f () \n{ \n    cat <<ONE |\nfirst\nONE\n  cat <<'TWO'\nsecond\nTWO\n\n}\n")]
    [InlineData("f(){ cat <<END\nx\nEND\necho after;\n}", "f () \n{ \n    cat <<END\nx\nEND\n\n    echo after\n}\n")]
    [InlineData("f(){ printf '%s' 'a\r\nb';\n}", "f () \n{ \n    printf '%s' 'a\r\nb'\n}\n")]
    [InlineData("f() (echo x)", "f () \n{ \n    ( echo x )\n}\n")]
    [InlineData("f(){ echo x; } >out", "f () \n{ \n    echo x\n} > out\n")]
    [InlineData("f(){ printf '%s\\n' \"a\\\"b\" \"a\\\\b\" \"a\\`b\" a'\"'b; }", "f () \n{ \n    printf '%s\\n' \"a\\\"b\" \"a\\\\b\" \"a\\`b\" a'\"'b\n}\n")]
    public async Task DefinitionOutput_PreservesSyntaxAndInvocation(string definition, string expected)
    {
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(definition).Commands));
        var bytes = Utf8Text.Encode(expected).Length;
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(function, output));
        var printed = Utf8Text.Decode(output.ToMemory());
        Assert.Equal(expected, printed);

        var reparsed = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(printed).Commands));
        using var repeated = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(reparsed, repeated));
        Assert.Equal(printed, Utf8Text.Decode(repeated.ToMemory()));

        const string Initial = "x=value;y=word;n=0; ";
        const string Invoke = "f a; printf '<%s,%s,%s>\\n' \"$?\" \"$n\" \"$x\"";
        var before = await EndToEndHarness.RunScriptAsync(Initial + definition + "; " + Invoke,
            EndToEndHarness.BuildTree([("/in", "input\n")]));
        Assert.NotEqual(124, before.ExitCode);
        using var exported = new BoundedByteMemoryBuilder(0, bytes, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppendExport(function, exported));
        using var exactExport = new BoundedByteMemoryBuilder(0, exported.Length, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppendExport(function, exactExport));
        Assert.Equal(exported.ToMemory().ToArray(), exactExport.ToMemory().ToArray());
        using var shortExport = new BoundedByteMemoryBuilder(0, exported.Length - 1, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.False(FunctionDefinitionFormatter.TryAppendExport(function, shortExport));
        Assert.InRange(shortExport.Length, 0, exported.Length - 1);
        foreach (var replay in new[] { printed, Utf8Text.Decode(exported.ToMemory()) + "\n" })
        {
            var after = await EndToEndHarness.RunScriptAsync(Initial + replay + Invoke,
                EndToEndHarness.BuildTree([("/in", "input\n")]));
            Assert.Equal(before.ExitCode, after.ExitCode);
            Assert.Equal(before.StdOut, after.StdOut);
            Assert.Equal(before.StdErr, after.StdErr);
            foreach (var path in new[] { "/out", "/err" })
            {
                var exists = await EndToEndHarness.FileExistsAsync(before.Host, path);
                Assert.Equal(exists, await EndToEndHarness.FileExistsAsync(after.Host, path));
                if (exists)
                    Assert.Equal(await EndToEndHarness.ReadFileAsync(before.Host, path),
                        await EndToEndHarness.ReadFileAsync(after.Host, path));
            }
        }
    }
}
