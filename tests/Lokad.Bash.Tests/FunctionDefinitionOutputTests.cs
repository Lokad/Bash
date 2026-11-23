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

    [Fact]
    public void WideDefinitionAdmitsTraversalBeforeSchedulingItsOperands()
    {
        const int bufferSize = 4096;
        var operands = string.Concat(Enumerable.Repeat(" data", 512));
        var words = "f(){ :" + operands + "; }";
        var literal = "f(){ : '" + operands[3..] + "'; }";
        Assert.Equal(words.Length, literal.Length);
        var wide = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(words).Commands));
        var narrow = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(literal).Commands));
        var governor = new MemoryGovernor(2 * bufferSize);
        using var output = new ByteMemoryBuilder(bufferSize, governor);

        Assert.Throws<MemoryBudgetExceededException>(() => FunctionDefinitionFormatter.TryAppend(wide, output));
        Assert.Equal(bufferSize, governor.ReservedBytes);

        output.Truncate(0);
        Assert.True(FunctionDefinitionFormatter.TryAppend(narrow, output));
        Assert.Equal(bufferSize, governor.ReservedBytes);
    }

    [Fact]
    public void ExportAdmitsPendingHereDocumentsSeparatelyFromRedirectionWork()
    {
        const int bufferSize = 2048;
        const int documents = 64;
        var ordinary = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :"
            + string.Concat(Enumerable.Repeat(" <Mark", documents)) + "; }").Commands));
        var bodies = string.Concat(Enumerable.Range(0, documents).Select(index => $"line{index}\nMark\n"));
        var queued = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :"
            + string.Concat(Enumerable.Repeat(" <<Mark", documents)) + "\n" + bodies + "}\n").Commands));
        var governor = new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes);
        using (var export = new BoundedByteMemoryBuilder(bufferSize, bufferSize, governor))
        {
            Assert.True(FunctionDefinitionFormatter.TryAppendExport(ordinary, export));
            Assert.Equal(bufferSize, governor.ReservedBytes);
        }

        var limited = new MemoryGovernor(governor.PeakBytes);
        using (var export = new BoundedByteMemoryBuilder(bufferSize, bufferSize, limited))
        {
            Assert.Throws<MemoryBudgetExceededException>(() => FunctionDefinitionFormatter.TryAppendExport(queued, export));
            Assert.Equal(bufferSize, limited.ReservedBytes);
        }

        using var admitted = new BoundedByteMemoryBuilder(bufferSize, bufferSize, governor);
        Assert.True(FunctionDefinitionFormatter.TryAppendExport(queued, admitted));
        Assert.Contains(bodies, Utf8Text.Decode(admitted.ToMemory()), StringComparison.Ordinal);
        Assert.Equal(bufferSize, governor.ReservedBytes);
    }

    [Fact]
    public void LongDescriptorNameEncodingReleasesItsScratchOnRefusal()
    {
        const int bufferSize = 2048;
        var name = new string('n', 1024);
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ {" + name + "}>out; }").Commands));
        var governor = new MemoryGovernor(bufferSize + 1024);
        using var output = new ByteMemoryBuilder(bufferSize, governor);

        Assert.Throws<MemoryBudgetExceededException>(() => FunctionDefinitionFormatter.TryAppend(definition, output));
        Assert.Equal(bufferSize, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void DescriptorNameKeepsItsBytesInDisplayAndExport(int length)
    {
        var name = new string('n', length);
        var source = "f(){ {" + name + "}>out; }";
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(source).Commands));
        var budget = new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes);
        using var display = new ByteMemoryBuilder(0, budget);
        using var export = new BoundedByteMemoryBuilder(0, 1024, budget);

        Assert.True(FunctionDefinitionFormatter.TryAppend(function, display));
        Assert.True(FunctionDefinitionFormatter.TryAppendExport(function, export));
        Assert.Equal("f () \n{ \n    {" + name + "}> out\n}\n", Utf8Text.Decode(display.ToMemory()));
        foreach (var bytes in new[] { display.ToMemory(), export.ToMemory() })
        {
            var parsed = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(bytes).Commands));
            var command = Assert.IsType<SimpleCommand>(Assert.IsType<GroupCommand>(parsed.Body).Body);
            Assert.Equal(name, Assert.Single(command.Redirections).VariableName);
        }
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

    [Fact]
    public async Task DefinitionRoundTripsPreserveQuoteGroupsAndReachedEffects()
    {
        const string script = "count(){ printf '<%s>' \"$#\"; }; "
            + "f(){ count \"$@$(printf x >>fact)$1\" \"$@\"\"$(true)\"; }; "
            + "f; export -f f count; bash -c 'f'; saved=$(declare -f f); unset -f f; eval \"$saved\"; f";

        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1><1><1>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal("xxx", result.Host.ReadFile("/fact"));
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task DefinitionRoundTripsKeepTheAdjacentOpeningOfATimedSubstitution()
    {
        const string source = "f(){ TIMEFORMAT=; value=$((time;);); printf '<%s:%s>' \"$?\" \"$value\"; }; "
            + "f; saved=$(declare -f f); unset -f f; eval \"$saved\"; f; export -f f; bash -c f";
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(source, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0:><0:><0:>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task DefinitionRoundTripsKeepADeferredAdjacentSubstitution()
    {
        const string source = """
            f(){ value=$((for ((i=7)); do : >body; break; done);); printf '<%s:%s>' "$?" "$value"; }
            saved=$(declare -f f)
            unset -f f
            eval "$saved"
            f
            export -f f
            bash -c f
            """;
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(source, host);

        const string Diagnostic = "syntax error: Arithmetic for header requires two semicolons.\n";
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2:><2:>", result.StdOut);
        Assert.Equal(Diagnostic + Diagnostic, result.StdErr);
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ReloadReportsTheRejectedCaptureShownInAFunctionListing()
    {
        const string script = "f(){ value=\"$( { cat <<DATA\nbody\nDATA\n}; printf extra)\"; printf tail; }\n"
            + "declare -f f\nsaved=$(declare -f f)\nunset -f f\neval \"$saved\"\nprintf unreached\n";
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(script, host);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("f () \n{ \n    value=\"$({ cat <<DATA\nbody\nDATA\n } printf extra)\";\n    printf tail\n}\n",
            result.StdOut);
        Assert.Equal("eval: line 13: syntax error near unexpected token `printf'\n"
            + "eval: line 13: ` } printf extra)\";'\n", result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ReloadReportsTheReachedTokenInsideANestedCaptureGroup()
    {
        const string script = "f(){ value=$( { { cat <<DATA\nbody\nDATA\n}; :; }; printf extra); printf tail; }\n"
            + "saved=$(declare -f f)\nunset -f f\neval \"$saved\"\nprintf unreached\n";
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(script, host);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("eval: line 12: syntax error near unexpected token `:'\n"
            + "eval: line 12: ` } :; }; printf extra);'\n", result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("$(printf \"%s\" \"", "\")")]
    [InlineData("$((printf \"%s\" \"", "\");)")]
    public void NestedRejectedCapturesHaveDisplayAndImportSpelling(string opener, string closer)
    {
        const string authored = "$( { cat <<DATA\nbody\nDATA\n}; printf extra)";
        const string shown = "$({ cat <<DATA\nbody\nDATA\n } printf extra)";
        var source = "f(){ value=\"" + opener + authored + closer + "\"; printf tail; }";
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(source).Commands));
        var budget = new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes);
        using var display = new ByteMemoryBuilder(0, budget);
        using var export = new BoundedByteMemoryBuilder(0, 1024, budget);

        Assert.True(FunctionDefinitionFormatter.TryAppend(definition, display));
        Assert.Equal("f () \n{ \n    value=\"" + opener + shown + closer + "\";\n    printf tail\n}\n",
            Utf8Text.Decode(display.ToMemory()));
        Assert.True(FunctionDefinitionFormatter.TryAppendExport(definition, export));
        Assert.Contains(authored, Utf8Text.Decode(export.ToMemory()));
        Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(export.ToMemory()).Commands));
    }

    // Both Bash pins agree before and after reloading the displayed definition.
    [Theory]
    [InlineData("printf '<%s>' \"$LINENO\"", "<1>:1|<7>:8")]
    [InlineData("printf '<%s>' \"$LINENO\"\nprintf '<%s>' \"$LINENO\"", "<2><3>:2|<9><10>:10")]
    [InlineData("printf '<%s>' \"$LINENO\" # skipped\n", "<2>:2|<8>:9")]
    [InlineData("case k in k) cat <<END\nbody\nEND\n;; esac\nprintf '<%s>' \"$LINENO\"",
        "body\n<13>:5|body\n<27>:20")]
    [InlineData("case k in k) cat <<END\nbody\nEND\n;& next) printf '<%s>' \"$LINENO\";; esac\nprintf '<%s>' \"$LINENO\"",
        "body\n<13><16>:5|body\n<30><33>:23")]
    [InlineData("while false; do cat <<END\nbody\nEND\ndone\nprintf '<%s>' \"$LINENO\"", "<11>:5|<23>:18")]
    public async Task DefinitionReloadPreservesCaptureCoordinates(string body, string expected)
    {
        var script = "f(){ v=$(" + body + "); printf '%s:%s' \"$v\" \"$LINENO\"; }\n"
            + "f; printf '|'\nsaved=$(declare -f f)\nunset -f f\neval \"$saved\"\nf\n";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    // Git Bash 5.2.37 output, except retained CR, equivalent quoting/substitution delimiters and flat elif chains.
    [Theory]
    // The escaped dollar outside the quotes remains separate from the next quoted group.
    [InlineData("f(){ printf '%s\\n' plain'single'\"double\\\"\\\\\"\\$\"\\$\"$(printf one)\"$(printf two)\"; cat <<'END'\nbody\nEND\n}", "f () \n{ \n    printf '%s\\n' plain'single'\"double\\\"\\\\\"\\$\"\\$\"$(printf one)\"$(printf two)\";\n    cat <<'END'\nbody\nEND\n\n}\n")]
    [InlineData("f(){ :;\n}", "f () \n{ \n    :\n}\n")]
    [InlineData("f(){ true || : `if`; printf ok; }", "f () \n{ \n    true || : `if`;\n    printf ok\n}\n")]
    [InlineData("f(){ false && : \"`)`\"; printf ok; }", "f () \n{ \n    false && : \"`)`\";\n    printf ok\n}\n")]
    [InlineData("f(){ printf '%s' `((n=1)); printf hi`; }", "f () \n{ \n    printf '%s' `((n=1)); printf hi`\n}\n")]
    [InlineData("f(){ printf '%s' `\\\n(printf hi)`; }", "f () \n{ \n    printf '%s' `\\\n(printf hi)`\n}\n")]
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
    [InlineData("f(){ { printf O; printf E >&2; } >out |& cat; }",
        "f () \n{ \n    { \n        printf O;\n        printf E 1>&2\n    } > out 2>&1 | cat\n}\n")]
    [InlineData("f(){ case \"$1\" in a|'b c') echo yes;; *) :;; esac;\n}", "f () \n{ \n    case \"$1\" in \n        a | 'b c')\n            echo yes\n        ;;\n        *)\n            :\n        ;;\n    esac\n}\n")]
    [InlineData("f(){ case esac\nin (esac) printf match;; esac; }", "f () \n{ \n    case esac in \n        (esac)\n            printf match\n        ;;\n    esac\n}\n")]
    [InlineData("f(){ case k in k) # empty\nesac; }",
        "f () \n{ \n    case k in \n        k)\n\n        ;;\n    esac\n}\n")]
    [InlineData("f(){ value=$(case k in k) ;;& k) printf body;; esac); printf \"<%s:%s>\" \"$?\" \"$value\"; }",
        "f () \n{ \n    value=$(case k in \n    k)\n\n    ;;&\n    k)\n        printf body\n    ;;\nesac);\n    printf \"<%s:%s>\" \"$?\" \"$value\"\n}\n")]
    [InlineData("f(){ case k in k) >out;; esac; }",
        "f () \n{ \n    case k in \n        k)\n            > out\n        ;;\n    esac\n}\n")]
    [InlineData("f(){ for x in a 'b c' \"$@\"; { printf '%s\\n' \"$x\"; }; }", "f () \n{ \n    for x in a 'b c' \"$@\";\n    do\n        printf '%s\\n' \"$x\";\n    done\n}\n")]
    [InlineData("f(){ [[ $x == a* && -n \"$y\" ]];\n}", "f () \n{ \n    [[ $x == a* && -n \"$y\" ]]\n}\n")]
    [InlineData("f(){ printf '%s' \"$([[ yes ]]; printf yes)\"; }",
        "f () \n{ \n    printf '%s' \"$([[ -n yes ]]; printf yes)\"\n}\n")]
    [InlineData("f(){ printf '<%s>' \"$(printf %s $'a\\nb' # skipped\n)\"; }",
        "f () \n{ \n    printf '<%s>' \"$(printf %s 'a\nb')\"\n}\n")]
    [InlineData("f(){ printf '<%s>' \"$(if false; then :; elif :; then printf yes; fi)\"; }",
        "f () \n{ \n    printf '<%s>' \"$(if false; then\n    :;\nelse\n    if :; then\n        printf yes;\n    fi;\nfi)\"\n}\n")]
    [InlineData("f(){ printf '<%s>' \"$(g(){ printf one; printf two\nprintf three; }; g)\"; }",
        "f () \n{ \n    printf '<%s>' \"$(function g () \n{ \n    printf one;\n    printf two\n\n    printf three\n}; g)\"\n}\n")]
    [InlineData("f(){ printf '<%s>' \"$( (cat <<END\none\nEND\n))\"; }",
        "f () \n{ \n    printf '<%s>' \"$( ( cat <<END\none\nEND\n ))\"\n}\n")]
    [InlineData("f(){ printf '<%s>' \"$(cat <<END && printf two\none\nEND\n)\"; }",
        "f () \n{ \n    printf '<%s>' \"$(cat <<END && \none\nEND\n printf two)\"\n}\n")]
    [InlineData("f(){ ((x+=1));\n}", "f () \n{ \n    ((x+=1))\n}\n")]
    [InlineData("f(){ n=1; printf \"<%s>\" \"$(((n+=2));)\"; printf \"[%s]\" \"$n\"; }",
        "f () \n{ \n    n=1;\n    printf \"<%s>\" \"$(((n+=2));)\";\n    printf \"[%s]\" \"$n\"\n}\n")]
    [InlineData("f(){ n=1; printf \"<%s>\" \"$(((n+=2))\n)\"; printf \"[%s]\" \"$n\"; }",
        "f () \n{ \n    n=1;\n    printf \"<%s>\" \"$(((n+=2))\n)\";\n    printf \"[%s]\" \"$n\"\n}\n")]
    [InlineData("f(){ printf '%s' \"$(((1))  ;  printf %s $'a\\tb'    )\"; }",
        "f () \n{ \n    printf '%s' \"$(((1))  ;  printf %s 'a\tb'    )\"\n}\n")]
    [InlineData("f(){ printf '%s' \"$(\\\n((1)); printf %s \"$(printf one # comment\n)\")\"; }",
        "f () \n{ \n    printf '%s' \"$(((1)); printf %s \"$(printf one)\")\"\n}\n")]
    [InlineData("f(){ g(){ printf '%s\\n' \"$1\"; }; g x;\n}", "f () \n{ \n    function g () \n    { \n        printf '%s\\n' \"$1\"\n    };\n    g x\n}\n")]
    [InlineData("f(){ echo x 2>err >out; cat <in; echo y >&2; true 3>&-;\n}", "f () \n{ \n    echo x 2> err > out;\n    cat < in;\n    echo y 1>&2;\n    true 3>&-\n}\n")]
    [InlineData("f(){ >out 2>err; }", "f () \n{ \n    > out 2> err\n}\n")]
    [InlineData("f(){ printf x | >out; }", "f () \n{ \n    printf x | > out\n}\n")]
    [InlineData("f(){ printf '%s' \"$(<in)\"; }", "f () \n{ \n    printf '%s' \"$(< in)\"\n}\n")]
    [InlineData("f(){ printf x 1>out; cat 0<in; printf y 3>other 1>&3; }",
        "f () \n{ \n    printf x > out;\n    cat < in;\n    printf y 3> other 1>&3\n}\n")]
    [InlineData("f(){ : <>in; : 0<&-; }", "f () \n{ \n    : 0<> in;\n    : 0>&-\n}\n")]
    [InlineData("f(){ : >&\"1\"; : >&01; }", "f () \n{ \n    : >&\"1\";\n    : 1>&1\n}\n")]
    [InlineData("f(){ printf '%s' \"$(cat 0<<'END'\nline\nEND\n)\"; }",
        "f () \n{ \n    printf '%s' \"$(cat <<'END'\nline\nEND\n)\"\n}\n")]
    [InlineData("f(){ x='a b' y+=c printf '%s\\n' \"$x\"; declare -a arr=([2]='a b' tail); arr+=(one);\n}", "f () \n{ \n    x='a b' y+=c printf '%s\\n' \"$x\";\n    declare -a arr=([2]='a b' tail);\n    arr+=(one)\n}\n")]
    [InlineData("f(){ printf '%s\\n' 'a b' \"$x\" \"a\\$x\" \"${x:-a b}\" a\\ b \"$(printf x)\";\n}", "f () \n{ \n    printf '%s\\n' 'a b' \"$x\" \"a\\$x\" \"${x:-a b}\" a\\ b \"$(printf x)\"\n}\n")]
    [InlineData("f(){ cat <<'END'\n$x\nEND\n\n}", "f () \n{ \n    cat <<'END'\n$x\nEND\n\n}\n")]
    [InlineData("f(){ cat <<END\n$x\nEND\n\n}", "f () \n{ \n    cat <<END\n$x\nEND\n\n}\n")]
    [InlineData("f(){ cat <<ONE | cat <<'TWO'\nfirst\nONE\nsecond\nTWO\n\n}", "f () \n{ \n    cat <<ONE |\nfirst\nONE\n  cat <<'TWO'\nsecond\nTWO\n\n}\n")]
    [InlineData("f(){ cat <<END\nx\nEND\necho after;\n}", "f () \n{ \n    cat <<END\nx\nEND\n\n    echo after\n}\n")]
    [InlineData("f(){ (cat <<END\nbody\nEND\n); printf '%s' after; }",
        "f () \n{ \n    ( cat <<END\nbody\nEND\n )\n    printf '%s' after\n}\n")]
    [InlineData("f(){ { { cat <<END\nbody\nEND\n}; :; }; printf '%s' after; }",
        "f () \n{ \n    { \n        { \n            cat <<END\nbody\nEND\n\n        }\n        :\n    };\n    printf '%s' after\n}\n")]
    [InlineData("f(){ case k in k) cat <<END\nbody\nEND\n;; other) :;; esac; printf '%s' after; }",
        "f () \n{ \n    case k in \n        k)\n            cat <<END\nbody\nEND\n\n        ;;\n        other)\n            :\n        ;;\n    esac\n    printf '%s' after\n}\n")]
    [InlineData("f(){ printf '%s' 'a\r\nb';\n}", "f () \n{ \n    printf '%s' 'a\r\nb'\n}\n")]
    [InlineData("f() (echo x)", "f () \n{ \n    ( echo x )\n}\n")]
    [InlineData("function f (printf '%s' \"$1\")", "f () \n{ \n    ( printf '%s' \"$1\" )\n}\n")]
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

    [Fact]
    public async Task DefinitionReloadPreservesAdjacentCaptureLineLayout()
    {
        const string script = "f(){ n=1; printf \"[%s]\" \"$(((n+=2))\n\n\nprintf \"<%s:%s>\" \"$n\" \"$LINENO\"\n)\"; "
            + "printf \"/%s:%s/\" \"$n\" \"$LINENO\"; }\ndeclare -f f\nf\nsaved=$(declare -f f)\nunset -f f\neval \"$saved\"\nf\n";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith("[<3:4>]/1:5/[<3:16>]/1:18/", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("\"${missing:-", "}tail\"", "}tail\"", 13)]
    [InlineData("$(( ", " + \\\n1 ))", " + 1 ))", 14)]
    public async Task DefinitionReloadRejectsCapturesRenderedInsideExpansionOperands(
        string opening, string closing, string displayedClosing, int line)
    {
        var script = "f(){ value=" + opening + "$({ cat <<DATA\nbody\nDATA\n}; printf extra)" + closing
            + "; }\ndeclare -f f\nsaved=$(declare -f f)\nunset -f f\neval \"$saved\"\nprintf 'loaded:%s\\n' \"$?\"\n";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("f () \n{ \n    value=" + opening + "$({ cat <<DATA\nbody\nDATA\n } printf extra)"
            + displayedClosing + "\n}\n", result.StdOut);
        Assert.Equal($"eval: line {line}: syntax error near unexpected token `printf'\n"
            + $"eval: line {line}: ` }} printf extra){displayedClosing}'\n", result.StdErr);
    }

    [Theory]
    [InlineData("((1)); # comment\\\nprintf wrong\nprintf kept", "[kept][kept]")]
    [InlineData("((1)); cat <<'END'\n$'a\\nb' literal\\\ncontinued\nEND\n", "['a\nb' literalcontinued]['a\nb' literalcontinued]")]
    public async Task AdjacentCaptureExecutesTheMatchedSpellingBeforeAndAfterReload(string body, string stdout)
    {
        var script = "f(){ printf '[%s]' \"$(" + body + ")\"; }\nf\nsaved=$(declare -f f)\nunset -f f\neval \"$saved\"\nf\n";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(stdout, result.StdOut);
        Assert.Empty(result.StdErr);
    }
}
