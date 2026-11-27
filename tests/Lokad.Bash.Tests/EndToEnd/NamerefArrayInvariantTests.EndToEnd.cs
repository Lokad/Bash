using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class NamerefArrayInvariantTestsEndToEnd
{
    // Storage contract, not Bash 5.2 parity: its cyclic-local array/nameref hybrids violate exclusive value kinds.
    [Theory]
    [InlineData("-a first=(value)", "0")]
    [InlineData("-A first=([key]=value)", "key")]
    public async Task Evaluation_CyclicLocalArrayConversion_PreservesExclusiveValueKinds(string declaration, string key)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "f() { local -n first=second second=first; local " + declaration
            + "; printf '<%s>\\n' \"${first[" + key + "]}\"; }; f");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<value>\n", result.StdOut);
        Assert.Equal("line 1: warning: first: circular name reference\nline 1: warning: first: circular name reference\n", result.StdErr);
    }

    [Theory]
    [InlineData("local -n slot=dest", "-n", "-an slot=()", 0)]
    [InlineData("local -n slot", "-An", "-An slot", 1)]
    [InlineData("local -nr slot=dest", "-An", "-Anr slot=()", 0)]
    [InlineData("local -n through=dest slot=through", "-nx", "-an slot=()", 0)]
    public async Task RejectedLocalNamerefArrayRetainsKindWithoutReferent(
        string setup, string flags, string declaration, int referenceStatus)
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outside; dest=target\nf(){\n"
            + $"  {setup}\n  local {flags} 'slot[]=valid'\n"
            + "  printf '<%s:%s>' \"$?\" \"${#slot[@]}\"; "
            + "[[ -R slot ]]; printf '<%s>' \"$?\"; [[ -v slot ]]; printf '<%s>\\n' \"$?\"; declare -p slot\n"
            + "}\nf\ndeclare -p slot dest\n");

        Assert.Equal($"<1:0><{referenceStatus}><1>\ndeclare {declaration}\ndeclare -- slot=\"outside\"\n"
            + "declare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("slot=changed", "<0>\ndeclare -an slot=()\n", "")]
    [InlineData("local -n slot=second", "<1>\ndeclare -an slot=()\n",
        "line 5: local: slot: reference variable cannot be an array\n")]
    [InlineData("unset -v slot", "<0>\ndeclare -an slot=()\n", "")]
    [InlineData("unset -n slot", "<0>\ndeclare -- slot\n", "")]
    public async Task RejectedLocalNamerefArrayUsesItsOwnBindingForLaterCommands(
        string command, string output, string error)
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outside; dest=target\nf(){\n"
            + "  local -n slot=dest\n  local -n 'slot[]=valid'\n"
            + $"  {command}; " + "printf '<%s>\\n' \"$?\"; declare -p slot\n"
            + "}\nf\ndeclare -p slot dest\n");

        Assert.Equal(output + "declare -- slot=\"outside\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n" + error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ClearingRejectedArrayNamerefInChildKeepsTheParentBinding()
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outside; dest=target\nf(){\n"
            + "  local -n slot=dest\n  local -An 'slot[]=valid'\n"
            + "  ( local +n slot; slot=changed; declare -p slot ); declare -p slot\n"
            + "}\nf\ndeclare -p slot dest\n");

        Assert.Equal("declare -A slot=([0]=\"changed\" )\ndeclare -An slot=()\n"
            + "declare -- slot=\"outside\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n ref=dest", "local +n 'ref[]='", "-an ref=()", "ref[]: bad array subscript")]
    [InlineData("local -n ref", "declare -A 'ref[]=value'", "-An ref", "ref[]: bad array subscript")]
    [InlineData("local -n ref", "local -ix 'ref[]=value'", "-ainx ref", "ref[]: bad array subscript")]
    [InlineData("local -n ref", "local -ix 'ref[]=two words'", "-an ref",
        "local: `two words': invalid variable name for name reference")]
    [InlineData("local -nr ref=dest", "local -A +n 'ref[]=value'", "-Anr ref=()", "local: ref: readonly variable")]
    [InlineData("local -nr through; local -n ref=through", "local -A 'ref[]=value'", "-An ref=()", "ref[]: bad array subscript")]
    public async Task EmptyLocalElementRejectionRetainsThePreparedNamerefArray(
        string setup, string command, string declaration, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync("ref=outer; dest=target\nf(){\n"
            + $"  {setup}\n  {command}\n"
            + "  printf 'reached=%s\\n' \"$?\"; declare -p ref dest\n"
            + "}\nf\ndeclare -p ref dest\n");

        Assert.Equal($"reached=1\ndeclare {declaration}\ndeclare -- dest=\"target\"\n"
            + "declare -- ref=\"outer\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal($"line 4: {diagnostic}\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n ref=dest", "local -n 'ref[]=value'", "-n ref=\"dest\"\ndeclare -a dest",
        "local: ref: reference variable cannot be an array")]
    [InlineData("local -n through=dest ref=through", "local +n 'ref[]=value'", "-n ref=\"through\"\ndeclare -a dest",
        "ref[]: bad array subscript")]
    [InlineData("declare -gn through=dest; local -n ref=through", "local -Ax 'ref[]=two words'", "-An ref=()",
        "local: `two words': invalid variable name for name reference")]
    public async Task EmptyLocalElementRejectionDistinguishesMissingTargetOwners(
        string setup, string command, string declaration, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync("ref=outer\nf(){\n"
            + $"  {setup}\n  {command}\n"
            + "  printf 'reached=%s\\n' \"$?\"; declare -p ref; declare -p dest 2>/dev/null || :\n"
            + "}\nf\ndeclare -p ref\n");

        Assert.Equal($"reached=1\ndeclare {declaration}\ndeclare -- ref=\"outer\"\n", result.StdOut);
        Assert.Equal($"line 4: {diagnostic}\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n slot=dest", "+n", "i++", "-an slot=()", 1, "local: `slot[i++]': not a valid identifier")]
    [InlineData("local -n slot", "", "0", "-an slot", 0, "local: `slot[0]': not a valid identifier")]
    [InlineData("local -n slot", "-A", "i++", "-An slot", 1, "local: `slot[i++]': not a valid identifier")]
    [InlineData("local -n slot", "-A", "i++ - 1", "-An slot", 1, "slot[i++ - 1]: bad array subscript")]
    public async Task RejectedLocalNamerefElementKeepsSelectorEffectsAndThePreparedArray(
        string setup, string flags, string selector, string declaration, int indexEffects, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outer; dest=target; i=0\nf(){\n"
            + $"  {setup}\n  local {flags} 'slot[{selector}]=value'\n"
            + "  printf '<%s:%s>\\n' \"$?\" \"$i\"; declare -p slot dest\n"
            + "}\nf\ndeclare -p slot dest\n");

        Assert.Equal($"<1:{indexEffects}>\ndeclare {declaration}\ndeclare -- dest=\"target\"\n"
            + "declare -- slot=\"outer\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal($"line 4: {diagnostic}\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RejectedLocalNamerefElementRetainsFieldEffectsAndVisitsLaterOperands()
    {
        var result = await EvaluationTestHelper.RunAsync("i=0; j=0\nf(){\n"
            + "  local -n slot\n  local -A 'slot[i++]'=entry$((j++)) after=kept\n"
            + "  printf '<%s:%s:%s>\\n' \"$?\" \"$i\" \"$j\"; declare -p slot after\n"
            + "}\nf\n");

        Assert.Equal("<1:1:1>\ndeclare -An slot\ndeclare -A after=([0]=\"kept\" )\n", result.StdOut);
        Assert.Equal("line 4: local: `slot[i++]': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n slot=dest", "+n", "slot=value$((j++))", "0:0:1", "-a slot=()")]
    [InlineData("local -n slot=dest", "+n", "slot[i++]=value$((j++))", "1:1:1", "-an slot=()")]
    [InlineData("local -n slot", "+n", "slot+=value$((j++))", "0:0:1", "-a slot")]
    [InlineData("local -n slot", "", "slot=value$((j++))", "0:0:1", "-an slot")]
    [InlineData("local -n slot", "", "'slot[i++]'=value$((j++))", "1:1:1", "-an slot")]
    public async Task LocalIndexedNamerefSeparatesPreparationFromTheInitializer(
        string setup, string change, string operand, string outcome, string listing)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            slot=outside; dest=target; i=0; j=0
            f() {
                {{setup}}
                local {{change}} -a {{operand}} after=kept
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p slot after dest
            }
            f
            declare -p slot dest
            """);

        Assert.Equal($"<{outcome}>\ndeclare {listing}\ndeclare -a after=([0]=\"kept\")\n"
            + "declare -- dest=\"target\"\ndeclare -- slot=\"outside\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal<byte>([
            .. "line 4: local: `"u8,
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
            .. "': not a valid identifier\n"u8
        ], result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -nr slot", "local -a slot", 0, "declare -anr slot\n", "")]
    [InlineData("local -nr dest; local -n slot=dest", "local -Au slot=fresh", 0,
        "declare -Anu slot=([0]=\"FRESH\" )\ndeclare -nr dest\n", "")]
    [InlineData("local -n dest; local -nr slot=dest", "local -A slot=fresh", 1,
        "declare -Anr slot=()\ndeclare -n dest\n", "local: slot: readonly variable\n")]
    [InlineData("local -n slot=dest", "local -n -Au slot=fresh", 1,
        "declare -n slot=\"dest\"\ndeclare -A dest\n", "local: slot: reference variable cannot be an array\n")]
    [InlineData("local -n slot", "local -a slot[i++]=42", 1, "declare -an slot\n",
        "local: `42': invalid variable name for name reference\n")]
    public async Task OrdinaryLocalArrayDeclarationPreparesTheSelectedNamerefBinding(
        string setup, string declaration, int status, string listing, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            slot=outside; i=0
            f() {
                {{setup}}
                {{declaration}}
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot
                declare -p dest 2>/dev/null || :
            }
            f
            declare -p slot
            """);

        Assert.Equal($"<{status}:0>\n{listing}declare -- slot=\"outside\"\n", result.StdOut);
        Assert.Equal(diagnostic.Length == 0 ? "" : "line 4: " + diagnostic, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("-n", "+n", 0, "-a slot=()", "")]
    [InlineData("-nr", "+n", 1, "-anr slot=()", "line 4: local: slot: readonly variable\n")]
    [InlineData("-n", "-n", 1, "-an slot=()", "line 4: local: slot: reference variable cannot be an array\n")]
    [InlineData("-nr", "-n", 1, "-anr slot=()", "line 4: local: slot: reference variable cannot be an array\n")]
    public async Task ChangingLocalNamerefInCompoundDeclarationPreservesTheReferentWrite(
        string aliasFlags, string change, int status, string listing, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            slot=outside; dest=target
            f() {
                local {{aliasFlags}} slot=dest
                local {{change}} -a slot=(fresh tail)
                printf '<%s>\n' "$?"
                declare -p slot dest
            }
            f
            declare -p slot dest
            """);

        Assert.Equal($"<{status}>\ndeclare {listing}\ndeclare -a dest=([0]=\"fresh\" [1]=\"tail\")\n"
            + "declare -- slot=\"outside\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal(diagnostic, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ClearingReadonlyLocalNamerefCanInitializeItsMissingTarget()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0; slot=outside
            f() {
                local -nr slot=dest
                local +n -a slot[i++]=fresh
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot dest
            }
            f
            declare -p slot
            """);

        Assert.Equal("<0:1>\ndeclare -nr slot=\"dest\"\ndeclare -a dest=([0]=\"fresh\")\ndeclare -- slot=\"outside\"\n",
            result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local +n -a slot=(fresh tail)")]
    [InlineData("declare +n -A slot=([key]=fresh)")]
    public async Task ReadonlyUnsetNamerefStopsCompoundPreparation(string declaration)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            f() {
                local -nr slot
                printf 'before\n'
                {{declaration}}
                printf 'after\n'
            }
            f
            printf 'outside:%s\n' "$?"
            """);

        Assert.Equal("before\noutside:1\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n slot", "local -a slot=(first)", "a", "[0]=\"first\"", "")]
    [InlineData("local -n slot", "local -A slot=([0]=first)", "A", "[0]=\"first\" ", "")]
    [InlineData("local -nr dest; local -n slot=dest", "local -a slot=(first)", "a", "[0]=\"first\"", "declare -nr dest\n")]
    public async Task CompoundArrayNamerefKeepsCellsAcrossAttributeRemoval(
        string setup, string declaration, string kind, string cells, string targetListing)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            slot=outside; i=0
            f() {
                {{setup}}
                {{declaration}}
                declare -p slot
                declare -p dest 2>/dev/null || :
                [[ -R slot ]]; r=$?
                [[ -v slot ]]
                printf '<%s:%s:%s:%s>\n' "$r" "$?" "${slot[i++]-hidden}" "${#slot[@]}"
                local +n slot
                printf '<%s:%s>\n' "${slot[0]}" "$i"
                declare -p slot
            }
            f
            declare -p slot
            """);

        Assert.Equal($"declare -{kind}n slot=({cells})\n{targetListing}<0:1:hidden:0>\n<first:1>\n"
            + $"declare -{kind} slot=({cells})\ndeclare -- slot=\"outside\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a", "")]
    [InlineData("A", " ")]
    public async Task CompoundRedeclarationAppendsAndReplacesArrayNamerefCells(string kind, string cellSeparator)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            f() {
                local -n slot; local -{{kind}} slot=([2]=prior)
                local -{{kind}} slot+=([2]=fresh$((i++)))
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot
                local -{{kind}} slot=()
                declare -p slot
            }
            i=0; f
            """);

        Assert.Equal($"<0:1>\ndeclare -{kind}n slot=([2]=\"fresh0\"{cellSeparator})\ndeclare -{kind}n slot=()\n",
            result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("+n", 0, "a", "")]
    [InlineData("-n", 1, "an", "line 4: local: slot: reference variable cannot be an array\n")]
    public async Task CompoundAppendWritesCellsBeforeNamerefFlagDispatch(string flag, int status, string attributes, string error)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0
            f() {
                local -n slot; local -a slot=([2]=prior)
                local {{flag}} -a slot+=([i+=2]=fresh)
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot
            }
            f
            """);

        Assert.Equal($"<{status}:2>\ndeclare -{attributes} slot=([2]=\"fresh\")\n", result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a", "[i+=2]", 2, "")]
    [InlineData("A", "[2]", 0, " ")]
    public async Task ReadonlyArrayNamerefAppendPublishesBeforeEvalReturns(
        string kind, string selector, int indexEffect, string cellSeparator)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; j=0
            f() {
                local -n slot; local -{{kind}}r slot=([2]=prior)
                eval 'local -{{kind}} slot+=({{selector}}=fresh$((j++)))'
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p slot
            }
            f
            """);

        Assert.Equal($"<1:{indexEffect}:1>\ndeclare -{kind}nr slot=([2]=\"fresh0\"{cellSeparator})\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("", "cannot convert indexed to associative array")]
    [InlineData("+n", "readonly variable")]
    [InlineData("-n", "reference variable cannot be an array")]
    public async Task ConflictingCompoundArrayKindReportsPreparationThenBuiltinFailure(string flag, string error)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            f() {
                local -n slot; local -ar slot=([2]=prior)
                local {{flag}} -A slot=([2]=fresh)
                printf '<%s>\n' "$?"
                declare -p slot
            }
            f
            """);

        Assert.Equal("<1>\ndeclare -anr slot=([2]=\"prior\")\n", result.StdOut);
        Assert.Equal($"line 3: slot: cannot convert indexed to associative array\nline 3: local: slot: {error}\n",
            result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ClearingReadonlyTakesPrecedenceOverChangingTheArrayNamerefKind()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local -n slot; local -ar slot=([2]=prior)
                local +r -A slot
                printf '<%s>\n' "$?"
                declare -p slot
            }
            f
            """);

        Assert.Equal("<1>\ndeclare -anr slot=([2]=\"prior\")\n", result.StdOut);
        Assert.Equal("line 3: local: slot: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a", "0:1:1", "a slot=([0]=\"fresh0\")", "")]
    [InlineData("A", "0:0:1", "A slot=([i++]=\"fresh0\" )", "")]
    [InlineData("ar", "1:0:0", "ar slot", "line 5: alias: readonly variable\n")]
    public async Task DirectCompoundAssignmentClearsAnUninitializedArrayNameref(
        string attributes, string effects, string listing, string error)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; j=0
            f() {
                local -n slot; local -{{attributes}} slot
                local -n alias=slot
                eval 'alias+=([i++]=fresh$((j++)))'
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p slot alias
            }
            f
            """);

        Assert.Equal($"<{effects}>\ndeclare -{listing}\ndeclare -n alias=\"slot\"\n", result.StdOut);
        Assert.Equal("line 5: warning: alias: removing nameref attribute\n" + error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("()", "()", "FFFFFFFFFFFFFFFF")]
    [InlineData("([257]=prior)", "([257]=\"prior\")", "0101")]
    public async Task DirectCompoundAssignmentRejectsAnInvalidProjectedNameBeforeExpansion(
        string initializer, string listing, string diagnosticBytes)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; j=0
            f() {
                local -n slot; local -a slot={{initializer}}
                eval 'slot=([i++]=fresh$((j++)))'
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p slot
            }
            f
            """);

        Assert.Equal($"<1:0:0>\ndeclare -an slot={listing}\n", result.StdOut);
        Assert.Equal<byte>([
            .. "line 4: `"u8, .. Convert.FromHexString(diagnosticBytes), .. "': not a valid identifier\n"u8
        ], result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(256, "([0]=\"last\" [1]=\"tail0\" [2]=\"seed\")", "")]
    [InlineData(1563581304, "([3]=\"outer\")", "declare -a x[2]=([0]=\"last\" [1]=\"tail0\" [2]=\"seed\")\n")]
    public async Task DirectCompoundAssignmentThroughAProjectedIndexResolvesItsSelectors(
        int maximumIndex, string globalCells, string literalListing)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; j=0; x=outer; slot=([3]=outer)
            f() {
                local -n slot; local -a slot=([{{maximumIndex}}]=prior)
                slot=([2]=seed [0]=early [-2]=tail$((j++)) [i++]=last)
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p slot x
                declare -p 'x[2]' 2>/dev/null || :
            }
            f
            declare -p slot
            """);

        Assert.Equal($"<0:1:1>\ndeclare -an slot=([{maximumIndex}]=\"prior\")\ndeclare -- x=\"outer\"\n"
            + literalListing + $"declare -a slot={globalCells}\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -A x=([0]=base)", 0, "declare -A x=([i++]=\"fresh0\" )\ndeclare -- dest=\"base\"\n")]
    [InlineData("local -n x=dest", 1, "declare -n x=\"dest\"\ndeclare -a dest=([0]=\"fresh0\")\n")]
    public async Task DirectCompoundAssignmentUsesTheProjectedDestination(
        string declaration, int indexEffect, string listing)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; j=0; dest=base
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; {{declaration}}
                slot=([i++]=fresh$((j++)))
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p slot x dest
            }
            f
            """);

        Assert.Equal($"<0:{indexEffect}:1>\ndeclare -an slot=([120]=\"prior\")\n" + listing, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DirectCompoundAssignmentChecksReadonlyOnTheProjectedDestination()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            f() {
                local x=outer; local -nr slot; eval 'local -a slot=([120]=prior)'
                slot=(first$((i++)))
                local -r x
                eval 'slot=(second$((i++)))'
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot x
            }
            f
            """);

        Assert.Equal("<1:1>\ndeclare -anr slot=([120]=\"prior\")\ndeclare -ar x=([0]=\"first0\")\n", result.StdOut);
        Assert.Equal("line 6: slot: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task QuotedLocalArrayInitializerUsesTheProjectedWholeBinding()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local x=before
                local -n slot; local -a slot=([120]=prior)
                declare -p x
                local -au 'slot=([0]=fresh)'
                printf '<%s>\n' "$?"
                declare -p slot x
            }
            f
            """);

        Assert.Equal("declare -a x=()\n<0>\ndeclare -an slot=([120]=\"prior\")\ndeclare -au x=([0]=\"FRESH\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task LocalArrayNamerefValidatesTheValueBeforeReadonlyOrSelectorEffects()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            f() {
                local -n slot; local -ar slot=([2]=prior)
                local -a 'slot[i++]=has space'
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot
            }
            f
            """);

        Assert.Equal("<1:0>\ndeclare -anr slot=([2]=\"prior\")\n", result.StdOut);
        Assert.Equal("line 4: local: `has space': invalid variable name for name reference\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a", "an alias=()", "FFFFFFFFFFFFFFFF")]
    [InlineData("A", "An alias=([0]=\"fresh\" )", "")]
    public async Task TypedLocalArrayInitializerCanPrepareTheAuthoredAlias(
        string kind, string aliasListing, string diagnosticBytes)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            f() {
                local -n slot; local -a slot=([2]=prior)
                local -n alias=slot
                local -{{kind}} alias=fresh
                printf '<%s>\n' "$?"
                declare -p slot alias
            }
            f
            """);

        Assert.Equal($"<0>\ndeclare -an slot=([2]=\"prior\")\ndeclare -{aliasListing}\n", result.StdOut);
        Assert.Equal<byte>(diagnosticBytes.Length == 0 ? [] : [
            .. "line 4: local: `"u8, .. Convert.FromHexString(diagnosticBytes), .. "': not a valid identifier\n"u8
        ], result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task LocalArrayInitializerWithEmptyProjectionWritesBeyondTheCallerLocal()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            slot=([3]=outer); i=0
            f() {
                local -n slot; local -a slot=([256]=prior)
                local -au 'slot[2+i++]=fresh'
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot
            }
            g() { local slot=middle; f; declare -p slot; }
            g
            declare -p slot
            """);

        Assert.Equal("<0:1>\ndeclare -anu slot=([256]=\"prior\")\ndeclare -- slot=\"middle\"\ndeclare -a slot=([2]=\"fresh\")\n",
            result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task LocalAssociativeArrayNamerefAppendsAtKeyZeroWithTheRequestedCase()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local -n slot; local -A slot=([0]=prior)
                local -Au slot+=fresh
                printf '<%s>\n' "$?"
                declare -p slot
            }
            f
            """);

        Assert.Equal("<0>\ndeclare -Anu slot=([0]=\"PRIORFRESH\" )\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task LocalArrayNamerefElementProjectionKeepsReadonlyOnTheLiteralLocal()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            x=outer
            f() {
                local -n slot; local -a slot=([1563581304]=prior)
                local -n alias=slot
                local -r alias
                local -a alias=fresh
                printf '<%s>\n' "$?"
                declare -p slot alias 'x[2]' x
            }
            f
            declare -p 'x[2]' 2>/dev/null || :
            """);

        Assert.Equal("<1>\ndeclare -an slot=([1563581304]=\"prior\")\ndeclare -n alias=\"slot\"\ndeclare -ar x[2]\ndeclare -- x=\"outer\"\n",
            result.StdOut);
        Assert.Equal("line 6: local: alias: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(1563581304)]
    public async Task ArrayNamerefReadsProjectedArrayValuesAndKeys(int maximumIndex)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; x=outer
            f() {
                local -n slot; local -a slot=([{{maximumIndex}}]=prior)
                local -a slot=21
                local -a 'slot[2]=23'
                printf '<%s:%s:%s:%s>\n' "$slot" "${slot[-1]}" "${#slot[@]}" "${slot@a}"
                printf '<key:%s>\n' "${!slot[@]}"
                [[ -v slot[i++] ]]
                printf '<%s:%s>\n' "$?" "$i"
            }
            f
            declare -p x
            """);

        Assert.Equal("<21:23:2:a>\n<key:0>\n<key:2>\n<0:1>\ndeclare -- x=\"outer\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ArrayNamerefReadsFollowSuccessiveProjectionsThroughAnAlias()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; local -n x; local -a x=([121]=prior); local -a y=(21 22)
                local -n alias=slot
                printf '<%s:%s:%s:%s>\n' "$alias" "${#alias}" "$((alias[i++]))" "${!alias}"
                printf '<%s>\n' "$i"
            }
            f
            """);

        Assert.Equal("<21:2:21:y>\n<1>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(120, "x")]
    [InlineData(1563581304, "x[2]")]
    public async Task DirectAppendUsesTheProjectedArrayValueAndBounds(int maximumIndex, string destination)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            x=outer
            f() {
                local -n slot; local -a slot=([{{maximumIndex}}]=prior)
                local -a slot=21; local -a 'slot[2]=23'
                slot+=41
                slot[-1]+=42
                declare -p slot '{{destination}}'
            }
            f
            declare -p x
            """);

        Assert.Equal($"declare -an slot=([{maximumIndex}]=\"prior\")\ndeclare -a {destination}=([0]=\"2141\" [2]=\"2342\")\ndeclare -- x=\"outer\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DirectElementAssignmentUsesTheProjectedAssociativeKey()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; local -A x=([0]=21 [i++]=23)
                slot[i++]=41
                printf '<%s:%s:%s>\n' "$i" "${x[0]}" "${x[i++]}"
            }
            f
            """);

        Assert.Equal("<0:21:41>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DirectNameAssignmentUsesTheBaseElementOfAMissingLiteralProjection()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            x=([0]=21 [2]=23)
            f() {
                local -nr slot; eval 'local -a slot=([1563581304]=prior)'
                slot+=41
                declare -p slot
            }
            f
            declare -p x
            """);

        Assert.Equal("declare -anr slot=([1563581304]=\"prior\")\ndeclare -a x=([0]=\"21\" [2]=\"2341\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ProjectedReadonlyAssignmentReportsTheAuthoredElementNameAfterSelection()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            f() {
                local -n slot; local -a slot=([120]=prior)
                local -ar x=(21 22)
                eval 'slot[i++]=41'
                printf '<%s:%s:%s>\n' "$?" "$i" "${x[0]}"
            }
            f
            """);

        Assert.Equal("<1:1:21>\n", result.StdOut);
        Assert.Equal("line 5: slot: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task WholeNameArithmeticReselectsTheStoredElementForWriting()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            j=0
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; local -n x='dest[j++]'; local -a dest=(21 22)
                printf '<%s:%s>\n' "$((slot++))" "$j"
                printf '<%s:%s>\n' "$((slot=41))" "$j"
                declare -p dest
            }
            f
            """);

        Assert.Equal("<21:2>\n<41:3>\ndeclare -a dest=([0]=\"21\" [1]=\"22\" [2]=\"41\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ArithmeticElementMutationKeepsTheNumberOnAMissingLiteralProjection()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0; x=([0]=21 [2]=23)
            f() {
                local -nr slot; eval 'local -a slot=([1563581304]=prior)'
                eval 'printf "<%s:%s>\n" "$((slot[i++]++))" "$i"'
            }
            f
            declare -p x
            """);

        Assert.Equal("<0:1>\ndeclare -a x=([0]=\"21\" [2]=\"23\")\n", result.StdOut);
        Assert.Equal("line 4: `x[2]': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ProjectedUnsetNamerefAppendReachesTheNearestCallerBinding()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            x=outer
            inner() {
                local -n slot; local -a slot=([120]=prior); unset x; local -n x
                slot+=dest
                declare -p x
            }
            outer() { local x=middle; inner; declare -p x; }
            outer
            declare -p x
            """);

        Assert.Equal("declare -n x\ndeclare -- x=\"middledest\"\ndeclare -- x=\"outer\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ArrayNamerefProjectionThroughAnOrdinaryElementReferenceRespectsSelectorRoles()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0; j=0
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; local -n x='dest[j++]'; local -a dest=(21 22)
                printf '<%s:%s:%s:%s>\n' "$slot" "${slot[i++]-fallback}" "${#slot[i++]}" "${!slot}"
                [[ -v slot ]]
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
            }
            f
            """);

        Assert.Equal("<21:fallback:0:dest[j++]>\n<1:1:1>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task EmptyProjectionAppendRecreatesTheAuthoredGlobal()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0; slot=(outer keep); declare -ai alias=(21 22)
            f() {
                local -n slot; local -a slot=([256]=prior); local -n alias=slot
                alias[i++]+=text
                declare -p slot alias
            }
            f
            printf '<%s>\n' "$i"
            declare -p slot alias
            """);

        Assert.Equal("declare -an slot=([256]=\"prior\")\ndeclare -n alias=\"slot\"\n<1>\ndeclare -a slot=([0]=\"outer\" [1]=\"keep\")\ndeclare -a alias=([0]=\"text\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task EmptyProjectionArithmeticReadsZeroAndRecreatesTheGlobalArray()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0; slot=(21 22)
            f() {
                local -n slot; local -a slot=([256]=prior)
                printf '<%s:%s>\n' "$((slot[i++]++))" "$i"
                declare -p slot
            }
            f
            declare -p slot
            """);

        Assert.Equal("<0:1>\ndeclare -an slot=([256]=\"prior\")\ndeclare -a slot=([0]=\"1\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task EmptyProjectionDefaultWritesTheReselectedElement()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0; slot=(21 22)
            f() {
                local -n slot; local -a slot=([256]=prior)
                printf '<%s:%s>\n' "${slot[i++]:=41}" "$i"
                declare -p slot
            }
            f
            declare -p slot
            """);

        Assert.Equal("<41:2>\ndeclare -an slot=([256]=\"prior\")\ndeclare -a slot=([1]=\"41\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("printf -v 'alias[i++]' %s 41", "41")]
    [InlineData("read -r 'alias[i++]' <<< '41 42'", "41 42")]
    public async Task EmptyProjectionBuiltinElementWritesRecreateTheAuthoredGlobal(string command, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; alias=(outer keep)
            f() {
                local -n slot; local -a slot=([256]=prior); local -n alias=slot
                {{command}}
                declare -p slot alias
            }
            f
            printf '<%s>\n' "$i"
            declare -p alias
            """);

        Assert.Equal($"declare -an slot=([256]=\"prior\")\ndeclare -n alias=\"slot\"\n<1>\ndeclare -a alias=([0]=\"{expected}\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("mapfile -t -O 1 slot <<< $'41 42\\n43'", "")]
    [InlineData("mapfile -t -O 1 -c 1 -C report slot <<< $'41 42\\n43'", "<1:41 42>\n<2:43>\n")]
    public async Task EmptyProjectionMapfileStartsAtTheOriginInAFreshArray(string command, string callbackOutput)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            declare -ai slot=(21 22)
            report() { printf '<%s:%s>\n' "$1" "$2"; }
            f() {
                local -n slot; local -a slot=([256]=prior)
                {{command}}
                declare -p slot
            }
            f
            declare -p slot
            """);

        Assert.Equal(callbackOutput + "declare -an slot=([256]=\"prior\")\ndeclare -a slot=([1]=\"41 42\" [2]=\"43\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ReadArrayReplacesAProjectedLiteralLocal()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            x=outer
            f() {
                local -n slot; local -a slot=([1563581304]=prior)
                local -a 'slot[2]=old'
                read -ra slot <<< '41 42'
                declare -p slot 'x[2]'
            }
            f
            declare -p x
            """);

        Assert.Equal("declare -an slot=([1563581304]=\"prior\")\ndeclare -a x[2]=([0]=\"41\" [1]=\"42\")\ndeclare -- x=\"outer\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("read -ra alias <<< '41 42'", "[0]=\"41\" [1]=\"42\"")]
    [InlineData("mapfile -t -O 1 alias <<< $'41 42\\n43'", "[1]=\"41 42\" [2]=\"43\"")]
    public async Task IndexedInputClearsAnUninitializedTerminalNameref(string command, string elements)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            slot=(outer keep)
            f() {
                local -n slot; local -a slot; local -n alias=slot
                {{command}}
                declare -p slot alias
            }
            f
            declare -p slot
            """);

        Assert.Equal($"declare -a slot=({elements})\ndeclare -n alias=\"slot\"\ndeclare -a slot=([0]=\"outer\" [1]=\"keep\")\n", result.StdOut);
        Assert.Equal("line 4: warning: alias: removing nameref attribute\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task IndexedInputClearsAnUninitializedAssociativeMarkBeforeRejectingTheKind()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local -n slot; local -A slot; local -n alias=slot
                mapfile -t alias <<< '41'
                printf '<%s>\n' "$?"
                declare -p slot alias
            }
            f
            """);

        Assert.Equal("<1>\ndeclare -A slot\ndeclare -n alias=\"slot\"\n", result.StdOut);
        Assert.Equal("line 3: warning: alias: removing nameref attribute\nline 3: mapfile: alias: not an indexed array\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("slot", "<0:0>\ndeclare -a x=([0]=\"41\" [1]=\"22\")\n")]
    [InlineData("'slot[i++]'", "<0:1>\ndeclare -a x=([0]=\"21\" [1]=\"22\")\n")]
    public async Task ReadThroughAProjectedReceiverUsesTheSelectedPrefixOwner(string operand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0
            f() {
                local -n slot; local -a slot=([120]=prior); local -a x=(21 22)
                x=temporary read -r {{operand}} <<< 41
                printf '<%s:%s>\n' "$?" "$i"
                declare -p x
            }
            f
            """);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ReadAtEofStillAssignsTheProjectedElement()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            f() {
                local -n slot; local -a slot=([120]=prior); local -a x=(21 22)
                read -r 'slot[i++]' <<'END'
            END
                printf '<%s:%s>\n' "$?" "$i"
                declare -p x
            }
            f
            """);

        Assert.Equal("<1:1>\ndeclare -a x=([0]=\"\" [1]=\"22\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ProjectedPrintfCountPersistsWhenTheFinalElementTargetIsInvalid()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0; j=0
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; local -n x='dest[j++]'; local -a dest=(21 22)
                printf -v 'slot[i++]' '%n%s' slot text
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p dest
            }
            f
            """);

        Assert.Equal("<1:1:1>\ndeclare -a dest=([0]=\"0\" [1]=\"22\")\n", result.StdOut);
        Assert.Equal("line 5: printf: `dest[j++]': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ReadonlyProjectedPrintfCountKeepsFormattingAvailable()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local -n slot; local -a slot=([120]=prior); local -ar x=(21 22)
                printf 'abc%n\n' slot
                printf '<%s>\n' "$?"
                declare -p x
            }
            f
            """);

        Assert.Equal("abc\n<0>\ndeclare -ar x=([0]=\"21\" [1]=\"22\")\n", result.StdOut);
        Assert.Equal("line 3: slot: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task GetoptsWritesThroughAMissingLiteralProjection()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            x=outer
            f() {
                local -nr slot; eval 'local -a slot=([1563581304]=prior)'
                getopts :a: slot -a
                printf '<%s:%s:%s>\n' "$?" "$OPTIND" "$OPTARG"
                declare -p slot
            }
            f
            declare -p x
            """);

        Assert.Equal("<0:2:a>\ndeclare -anr slot=([1563581304]=\"prior\")\ndeclare -a x=([0]=\"outer\" [2]=\":\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task LetReselectsTheSavedElementForEachTemporaryScalarWrite()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            j=0
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; local -n x='dest[j++]'; local -a dest=(21 22)
                slot=temporary let 'slot++' 'slot+=2'
                printf '<%s:%s>\n' "$?" "$j"
                declare -p slot dest
            }
            f
            """);

        Assert.Equal("<0:2>\ndeclare -an slot=([120]=\"prior\")\ndeclare -a dest=([0]=\"1\" [1]=\"3\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task IndirectLetOutputKeepsUsingTheActiveReceiverPrefixForReads()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local -n slot; local -a slot=([120]=prior); local -a x=(21 22)
                x=temporary let 'slot++' 'slot+=2'
                declare -p x
            }
            f
            """);

        Assert.Equal("declare -a x=([0]=\"2\" [1]=\"22\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("slot+=temporary declare -p 'x[2]'", "declare -x x[2]=\"21temporary\"\n", 0)]
    [InlineData("slot=temporary unset -v 'slot[i++]'", "", 1)]
    public async Task ProjectedLiteralPrefixIsVisibleAndRestoresTheCaller(string command, string output, int index)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0
            f() {
                local -n slot; local -a slot=([1563581304]=prior); local -a slot=21; local -a 'slot[2]=23'
                {{command}}
                printf '<%s>\n' "$i"
                declare -p 'x[2]'
            }
            f
            """);

        Assert.Equal(output + $"<{index}>\ndeclare -a x[2]=([0]=\"21\" [2]=\"23\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ProjectedElementPrefixAppendUsesTheStoredSpellingWithoutEvaluatingIt()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            j=0
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; local -n x='dest[j++]'; local -a dest=(21 22)
                slot+=temporary declare -p slot
                printf '<%s>\n' "$j"
                declare -p slot dest
            }
            f
            """);

        Assert.Equal("declare -x slot=\"dest[j++]temporary\"\n<0>\ndeclare -an slot=([120]=\"prior\")\ndeclare -a dest=([0]=\"21\" [1]=\"22\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("unset 'slot[i++]'", "<0:1:0>\ndeclare -- slot\ndeclare -a dest=([0]=\"21\" [1]=\"22\")\n")]
    [InlineData("unset -v 'slot[i++]'", "<0:0:1>\ndeclare -an slot=([120]=\"prior\")\ndeclare -a dest=([1]=\"22\")\n")]
    public async Task ProjectedUnsetChoosesItsSelectorAfterCheckingTheFunctionNamespace(string command, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; j=0
            slot() { :; }
            f() {
                local -n slot; local -a slot=([120]=prior)
                unset x; local -n x='dest[j++]'; local -a dest=(21 22)
                {{command}}
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p slot dest
            }
            f
            declare -F slot
            """);

        Assert.Equal(expected + "slot\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(120, "<function>\ndeclare -an slot=([120]=\"prior\")\ndeclare -- x\n")]
    [InlineData(256, "<>\ndeclare -an slot=([256]=\"prior\")\ndeclare -a x=([0]=\"21\" [1]=\"22\")\n")]
    public async Task WholeProjectedUnsetGivesAReachedVariablePrecedenceOverAFunction(int maximumIndex, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            slot() { :; }
            f() {
                local -n slot; local -a slot=([{{maximumIndex}}]=prior); local -a x=(21 22)
                unset slot
                printf '<%s>\n' "$(type -t slot)"
                declare -p slot x
            }
            f
            """);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task WholeProjectedUnsetKeepsTheLiteralLocalUnassigned()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            x=outer
            f() {
                local -n slot; local -a slot=([1563581304]=prior); local -a slot=21
                unset -v slot
                declare -p slot 'x[2]' x
            }
            f
            declare -p x
            """);

        Assert.Equal("declare -an slot=([1563581304]=\"prior\")\ndeclare -- x[2]\ndeclare -- x=\"outer\"\ndeclare -- x=\"outer\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ProjectedElementUnsetChecksTheReachedReadonlyBindingBeforeTheSelector()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            f() {
                local -n slot; local -a slot=([120]=prior); local -ar x=(21 22)
                unset -v 'slot[i++]'
                printf '<%s:%s>\n' "$?" "$i"
                declare -p x
            }
            f
            """);

        Assert.Equal("<1:0>\ndeclare -ar x=([0]=\"21\" [1]=\"22\")\n", result.StdOut);
        Assert.Equal("line 4: unset: x: cannot unset: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ProjectedUnsetPreservesTheUninitializedTerminalReference()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            f() {
                local -n slot; local -a slot=([120]=prior); unset x; local -n x
                unset -v 'slot[i++]'
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot x
            }
            f
            """);

        Assert.Equal("<0:0>\ndeclare -- slot\ndeclare -n x\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ProjectedUnsetKeepsAReadonlyReferenceSelectedByItsOwnName()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0
            slot() { :; }
            f() {
                local -nr slot; eval 'local -a slot=([1563581304]=prior)'
                unset -v 'slot[i++]'
                printf '<%s:%s>\n' "$?" "$i"
                unset 'slot[i++]'
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot
            }
            f
            """);

        Assert.Equal("<0:0>\n<1:0>\ndeclare -anr slot=([1563581304]=\"prior\")\n", result.StdOut);
        Assert.Equal("line 7: unset: slot: cannot unset: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("'alias[i++]'")]
    public async Task EmptyProjectionUnsetClearsTheAuthoredLocalWithoutSelectingAnElement(string operand)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0; alias=(outer keep)
            f() {
                local -n slot; local -a slot=([256]=prior); local -n alias=slot
                unset -v {{operand}}
                declare -p slot alias
            }
            f
            printf '<%s>\n' "$i"
            declare -p alias
            """);

        Assert.Equal("declare -an slot=([256]=\"prior\")\ndeclare -- alias\n<0>\ndeclare -a alias=([0]=\"outer\" [1]=\"keep\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(120, "x")]
    [InlineData(1563581304, "x[2]")]
    public async Task DefaultAssignmentSelectsTheProjectedArrayAfterExpansion(int maximumIndex, string destination)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            i=0
            f() {
                local -n slot; local -a slot=([{{maximumIndex}}]=prior)
                printf '<element:%s>\n' "${slot[i++]:=37}"
                printf '<name:%s>\n' "${slot:=40}"
                printf '<index:%s>\n' "$i"
                declare -p slot '{{destination}}'
            }
            f
            """);

        Assert.Equal($"<element:37>\n<name:40>\n<index:2>\ndeclare -an slot=([{maximumIndex}]=\"prior\")\n"
            + $"declare -a {destination}=([0]=\"40\" [1]=\"37\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task DefaultAssignmentWithoutALiteralLocalDistinguishesNameAndSelector()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            i=0; x=older
            f() {
                local -nr slot; eval 'local -a slot=([1563581304]=prior)'
                printf '<name:%s>\n' "${slot:=37}"
                eval 'printf "<element:%s>\n" "${slot[i++]:=42}"'
                printf '<%s:%s>\n' "$?" "$i"
                declare -p slot x
            }
            f
            """);

        Assert.Equal("<name:older>\n<1:2>\ndeclare -anr slot=([1563581304]=\"prior\")\ndeclare -a x=([0]=\"older\" [2]=\"37\")\n",
            result.StdOut);
        Assert.Equal("line 5: eval: `x[2]': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("-a slot=([i++]=value$((j++)))", "anr slot=([0]=\"value0\")")]
    [InlineData("-A slot=([key$((i++))]=value$((j++)))", "Anr slot=([key0]=\"value0\" )")]
    public async Task ReadonlyCompoundNamerefPublishesBeforeTheNestedReaderReturns(
        string declaration, string listing)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            slot=outside; i=0; j=0
            f() {
                local -nr slot
                eval 'local +n {{declaration}}'
                printf '<%s:%s:%s>\n' "$?" "$i" "$j"
                declare -p slot
            }
            f
            declare -p slot
            """);

        Assert.Equal($"<1:1:1>\ndeclare -{listing}\ndeclare -- slot=\"outside\"\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task IndexedArrayNamerefUsesTheProjectedMaximumIndex()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local -n slot
                local -a slot=([257]=cell)
                printf '<%s>\n' "${!slot}"
                printf '%s' "${slot}"
                printf 'unreached\n'
            }
            f
            printf '<status:%s>\n' "$?"
            """);

        Assert.Equal<byte>([.. "<"u8, 0x01, .. ">\n<status:1>\n"u8], result.Host.StandardOutput.Content.ToArray());
        Assert.Equal<byte>([
            .. "line 5: "u8, 0x01, 0x01, .. ": invalid variable name for name reference\n"u8
        ], result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task EmptyArrayNamerefSpellingLeavesStoredCellsIntact()
    {
        var result = await EvaluationTestHelper.RunAsync("""
            f() {
                local -n slot
                local -a slot=(cell)
                printf '<%s>\n' "${slot[0]:=fallback}"
                printf -v 'slot[0]' '%s' fresh
                printf '<status:%s>\n' "$?"
                declare -p slot
            }
            f
            """);

        Assert.Equal("<fallback>\n<status:0>\ndeclare -an slot=([0]=\"cell\")\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("$slot")]
    [InlineData("${slot:-fallback$((i++))}")]
    [InlineData("${slot@a}")]
    [InlineData("$alias")]
    public async Task MixedIndexedNamerefWholeReadsDiscardTheCallingUnit(string operand)
    {
        var script = "slot=outer; dest=target; i=0\nf() {\n"
            + "  local -n slot=dest\n  local -n 'slot[]='\n  local -n alias=slot\n"
            + $"  printf 'read:%s' \"{operand}\"\n"
            + "  : >inside\n}\nf || : >fallback\n"
            + "printf '<%s:%s:%s:%s:%s>' \"$?\" \"${PIPESTATUS[*]}\" \"$slot\" \"$dest\" \"$i\"\n";
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        await using var host = new TestHost { MemoryBudget = governor };

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:1:outer:target:0>", host.StandardOutput.TextContent);
        Assert.Equal<byte>([
            .. "line 4: local: slot: reference variable cannot be an array\nline 6: "u8,
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
            .. ": invalid variable name for name reference\n"u8
        ], host.StandardError.Content.ToArray());
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("slot[i++]=value$((j++))", "<1:1:outer:target:1:1>")]
    [InlineData("printf '%s' \"${slot[i++]:=value$((j++))}\"", "<2:2:outer:target:2:1>")]
    public async Task MixedIndexedNamerefElementWritesRejectAfterSelectingTheIndex(string command, string output)
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outer; dest=target; i=0; j=0\nf() {\n"
            + "  local -n slot=dest\n  local -n 'slot[]='\n"
            + $"  {command}\n"
            + "  printf inside\n}\nf; printf same\n"
            + "printf '<%s:%s:%s:%s:%s:%s>' \"$?\" \"${PIPESTATUS[*]}\" \"$slot\" \"$dest\" \"$i\" \"$j\"\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Equal<byte>([
            .. "line 4: local: slot: reference variable cannot be an array\nline 5: `"u8,
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
            .. "': not a valid identifier\n"u8
        ], result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Fact]
    public async Task MixedAssociativeNamerefElementReadUsesArithmeticButLengthAndUnsetSkipTheSelector()
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outer; dest=target; i=0\nf() {\n"
            + "  local -n slot=dest\n  local -An 'slot[]='\n"
            + "  printf '<%s:%s:%s>' \"${#slot[i++]}\" \"${slot[i++]}\" \"$i\"\n"
            + "  unset 'slot[i++]'; printf '<%s:%s>' \"$?\" \"$i\"\n"
            + "}\nf\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0::1><0:1>", result.StdOut);
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n", result.StdErr);
    }

    [Fact]
    public async Task MixedIndexedNamerefIndirectionKeepsTheReachedSpellingBytes()
    {
        var result = await EvaluationTestHelper.RunAsync("dest=target\nf() {\n"
            + "  local -n slot=dest\n  local -n 'slot[]='\n  local -n alias=slot\n"
            + "  printf '<%s><%s><%s>' \"${!alias}\" \"${!slot:0:1}\" \"${!slot@a}\"\n"
            + "}\nf\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal<byte>([
            .. "<"u8, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, .. "><"u8, 0xff, .. "><>"u8
        ], result.Host.StandardOutput.Content.ToArray());
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n", result.StdErr);
    }

    [Fact]
    public async Task MixedAssociativeNamerefUsesTheBindingNameInTheReadDiagnostic()
    {
        var result = await EvaluationTestHelper.RunAsync("dest=target\nf() {\n"
            + "  local -n slot=dest\n  local -An 'slot[]='\n"
            + "  printf 'value:%s' \"$slot\"\n  printf inside\n}\nf\nprintf 'after:%s' \"$?\"\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("after:1", result.StdOut);
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n"
            + "line 5: slot: invalid variable name for name reference\n", result.StdErr);
    }

    [Fact]
    public async Task UnbracedReadOfUninitializedArrayNamerefExpandsToEmptyBeforeABracedReadFails()
    {
        var result = await EvaluationTestHelper.RunAsync("f() {\n"
            + "  local -n slot\n  local -n 'slot[]='\n"
            + "  (printf '<%s>' \"$slot\")\n"
            + "  printf 'braced:%s' \"${slot}\"\n  printf inside\n"
            + "}\nf\nprintf 'after:%s' \"$?\"\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<>after:1", result.StdOut);
        Assert.Equal<byte>([
            .. "line 3: local: slot: reference variable cannot be an array\nline 5: "u8,
            0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
            .. ": invalid variable name for name reference\n"u8
        ], result.Host.StandardError.Content.ToArray());
    }
}
