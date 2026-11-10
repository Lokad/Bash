using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class FieldSeparatorsTests
{
    [Fact]
    public void UnsplitWordsAndJoinsDoNotBuildMembershipIndexes()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText("é:")));
        var separators = FieldSeparators.FromVariables(variables);
        Assert.False(separators.IsEmpty);
        Assert.Equal(ShellValue.FromText("é"), separators.First);

        var budget = new ExpansionBudget().Words;
        var builder = new ExpandedWordBuilder(variables, budget, ExpansionPurpose.Scalar, ImplicitEmptyPolicy.Preserve);
        builder.AppendJoinedFields([ShellValue.FromText("left"), ShellValue.FromText("right")],
            allowWordSplitting: false, globQuoted: true);
        Assert.Equal(ShellValue.FromText("leftéright"), Assert.Single(builder.Build()).ToValue(budget));

        // Inspect lazy state directly instead of relying on runtime allocation counters.
        var characters = typeof(FieldSeparators).GetField("_characters",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var bytes = typeof(FieldSeparators).GetField("_byteMembership",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(characters);
        Assert.NotNull(bytes);
        Assert.Null(characters.GetValue(separators));
        Assert.Null(bytes.GetValue(separators));

        var clone = variables.Clone();
        var shared = FieldSeparators.FromVariables(clone);
        Assert.Same(separators, shared);
        Assert.True(shared.Contains(new Rune('é')));
        var characterIndex = characters.GetValue(separators);
        Assert.NotNull(characterIndex);
        Assert.Null(bytes.GetValue(separators));
        Assert.True(separators.Contains(new Rune(':')));
        Assert.Same(characterIndex, characters.GetValue(separators));

        Assert.True(shared.Contains((byte)0xA9));
        var byteIndex = bytes.GetValue(separators);
        Assert.NotNull(byteIndex);
        Assert.True(separators.Contains((byte)':'));
        Assert.Same(byteIndex, bytes.GetValue(separators));
        Assert.Same(characterIndex, characters.GetValue(separators));
    }

    [Fact]
    public async Task ClonedScopesCanInitializeMembershipConcurrently()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromBytes(new byte[] { 0xC3, 0xA9, 0xFF })));
        var separators = FieldSeparators.FromVariables(variables);
        var clones = Enumerable.Range(0, 8).Select(_ => variables.Clone()).ToArray();

        await Task.WhenAll(clones.Select(clone => Task.Run(() =>
        {
            var shared = FieldSeparators.FromVariables(clone);
            Assert.Same(separators, shared);
            Assert.True(shared.Contains(new Rune('é')));
            Assert.False(shared.Contains(Rune.ReplacementChar));
            Assert.True(shared.Contains((byte)0xFF));
            Assert.True(shared.Contains((byte)0xA9));
            Assert.False(shared.Contains((byte)':'));
        })));
    }

    [Theory]
    [InlineData("IFS=:; value=a:b; printf '<%s>' $value; IFS=,; read -r a b <<< c,d; printf '<%s><%s>' \"$a\" \"$b\"; unset IFS; value='e f'; printf '<%s>' $value", "<a><b><c><d><e><f>")]
    [InlineData("separator=:; declare -n IFS=separator; value=a:b; printf '<%s>' $value; separator=,; read -r a b <<< c,d; printf '<%s><%s>' \"$a\" \"$b\"; value=e,f; printf '<%s>' $value", "<a><b><c><d><e><f>")]
    [InlineData("IFS=:; value=a:b; printf '<%s>' $value; IFS=, read -r a b <<< c,d; printf '<%s><%s>' \"$a\" \"$b\"; printf '<%s>' $value", "<a><b><c><d><a><b>")]
    [InlineData("IFS=:; value=a:b; printf '<%s>' $value; f() { local IFS=,; read -r a b <<< c,d; printf '<%s><%s>' \"$a\" \"$b\"; }; f; printf '<%s>' $value", "<a><b><c><d><a><b>")]
    [InlineData("IFS=:; value=a:b; printf '<%s>' $value; (IFS=,; read -r a b <<< c,d; printf '<%s><%s>' \"$a\" \"$b\"); printf '<%s>' $value", "<a><b><c><d><a><b>")]
    [InlineData("IFS=$'\\377'; value=$'a\\377b'; printf '<%s>' $value; read -r a b <<< \"$value\"; printf '<%s><%s>' \"$a\" \"$b\"", "<a><b><a><b>")]
    public async Task ReadAndWordSplittingFollowBindingChanges(string script, string output)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public void CacheTracksEmptyUnsetAndArrayBindings()
    {
        var variables = Variables.CreateEmpty();
        var defaults = FieldSeparators.FromVariables(variables);
        Assert.True(defaults.Contains(new Rune(' ')));

        Assert.True(variables.TrySetScalar("IFS", ShellValue.Empty));
        var empty = FieldSeparators.FromVariables(variables);
        Assert.True(empty.IsEmpty);
        Assert.Same(empty, FieldSeparators.FromVariables(variables));
        Assert.False(empty.Contains(new Rune(' ')));

        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
        var colon = FieldSeparators.FromVariables(variables);
        Assert.True(colon.Contains(new Rune(':')));
        Assert.False(colon.Contains(new Rune(' ')));
        Assert.True(variables.TryUnset("IFS", UnsetFlags.None));
        Assert.Same(defaults, FieldSeparators.FromVariables(variables));

        Assert.True(variables.TryAssignArray("IFS", [ShellValue.FromText(",")], ArrayKind.Indexed));
        // The existing scalar-only IFS contract uses defaults for array bindings.
        Assert.Same(defaults, FieldSeparators.FromVariables(variables));
        Assert.True(colon.Contains(new Rune(':')));
    }

    [Fact]
    public void CacheTracksNamerefTargetChangesAndCycles()
    {
        var variables = Variables.CreateEmpty();
        var defaults = FieldSeparators.FromVariables(variables);
        Assert.True(variables.TrySetScalar("separator", ShellValue.FromText(",")));
        Assert.True(variables.TrySetNameref("IFS", ShellValue.FromText("separator")));
        var comma = FieldSeparators.FromVariables(variables);
        Assert.True(comma.Contains(new Rune(',')));

        Assert.True(variables.TrySetScalar("separator", ShellValue.FromText(":")));
        var colon = FieldSeparators.FromVariables(variables);
        Assert.NotSame(comma, colon);
        Assert.True(colon.Contains(new Rune(':')));
        Assert.False(colon.Contains(new Rune(',')));

        Assert.True(variables.TrySetNameref("IFS", ShellValue.FromText("other")));
        Assert.Same(defaults, FieldSeparators.FromVariables(variables));
        Assert.True(variables.TrySetScalar("other", ShellValue.Empty));
        Assert.True(FieldSeparators.FromVariables(variables).IsEmpty);
        Assert.True(variables.TryUnset("other", UnsetFlags.None));
        Assert.True(variables.TrySetNameref("other", ShellValue.FromText("IFS")));
        Assert.Same(defaults, FieldSeparators.FromVariables(variables));
    }

    [Fact]
    public void ClonesShareImmutableMembershipButTrackTheirOwnBindings()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
        var colon = FieldSeparators.FromVariables(variables);
        var clone = variables.Clone();
        Assert.Same(colon, FieldSeparators.FromVariables(clone));

        Assert.True(clone.TrySetScalar("IFS", ShellValue.FromText(",")));
        var comma = FieldSeparators.FromVariables(clone);
        Assert.NotSame(colon, comma);
        Assert.True(comma.Contains(new Rune(',')));
        Assert.Same(colon, FieldSeparators.FromVariables(variables));

        variables.CopyBindingFrom(clone, "IFS");
        var copied = FieldSeparators.FromVariables(variables);
        Assert.True(copied.Contains(new Rune(',')));
        Assert.True(copied.Bytes.Equals(comma.Bytes));
        Assert.False(copied.Contains(new Rune(':')));
        Assert.True(colon.Contains(new Rune(':')));
    }

    [Fact]
    public void CacheUsesMemorySliceIdentityAndIgnoresUnrelatedMutations()
    {
        var bytes = new byte[] { (byte)',', (byte)':' };
        var variables = Variables.CreateEmpty();
        var commaValue = ShellValue.FromBytes(bytes.AsMemory(0, 1));
        Assert.True(variables.TrySetScalar("IFS", commaValue));
        var comma = FieldSeparators.FromVariables(variables);
        Assert.True(variables.TrySetScalar("unrelated", ShellValue.FromText("changed")));
        Assert.Same(comma, FieldSeparators.FromVariables(variables));
        Assert.True(variables.TrySetScalar("IFS", commaValue));
        Assert.Same(comma, FieldSeparators.FromVariables(variables));

        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromBytes(bytes.AsMemory(1, 1))));
        var colon = FieldSeparators.FromVariables(variables);
        Assert.NotSame(comma, colon);
        Assert.True(colon.Contains(new Rune(':')));
        Assert.False(colon.Contains(new Rune(',')));
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
        Assert.NotSame(colon, FieldSeparators.FromVariables(variables));
    }

    [Theory]
    [InlineData("", 0x7A, false, 0xFF, false)]
    [InlineData("FF", 0xFF, false, 0xFF, true)]
    [InlineData("EFBFBD", 0xFFFD, true, 0xFF, false)]
    [InlineData("E282AC", 0x20AC, true, 0x82, true)]
    [InlineData("F09F9982", 0x1F642, true, 0x82, true)]
    [InlineData("80", 0x80, false, 0x80, true)]
    [InlineData("C280", 0x80, true, 0x80, true)]
    [InlineData("E2", 0xFFFD, false, 0xE2, true)]
    [InlineData("E241", 0x41, true, 0xE2, true)]
    public void MembershipKeepsValidScalarsSeparateFromMalformedByteFallback(
        string hex, int scalar, bool hasScalar, byte rawByte, bool hasByte)
    {
        var variables = Variables.CreateEmpty();
        var value = ShellValue.FromBytes(Convert.FromHexString(hex));
        Assert.True(variables.TrySetScalar("IFS", value));
        var separators = FieldSeparators.FromVariables(variables);

        Assert.Equal(hasScalar, separators.Contains(new Rune(scalar)));
        Assert.Equal(hasByte, separators.Contains(rawByte));
        Assert.True(value.Bytes.Equals(separators.Bytes));
        Assert.Same(separators, FieldSeparators.FromVariables(variables));
    }
}
