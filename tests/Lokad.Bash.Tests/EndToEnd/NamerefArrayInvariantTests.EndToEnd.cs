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
    [InlineData("local -n slot", "-a", "i++", "-an slot", 1, "local: `slot[i++]': not a valid identifier")]
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
    [InlineData("local -n slot=dest", "slot=value$((j++))", "0:0:1", "-a slot=()")]
    [InlineData("local -n slot=dest", "slot[i++]=value$((j++))", "1:1:1", "-an slot=()")]
    [InlineData("local -n slot", "slot+=value$((j++))", "0:0:1", "-a slot")]
    public async Task ClearingLocalIndexedNamerefSeparatesPreparationFromTheInitializer(
        string setup, string operand, string outcome, string listing)
    {
        var result = await EvaluationTestHelper.RunAsync($$"""
            slot=outside; dest=target; i=0; j=0
            f() {
                {{setup}}
                local +n -a {{operand}} after=kept
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
