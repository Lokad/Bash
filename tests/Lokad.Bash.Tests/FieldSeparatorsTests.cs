using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class FieldSeparatorsTests : IDisposable
{
    private readonly MemoryGovernor _governor = new(TestMemoryBudget.EvaluationBytes);
    private readonly EvaluationMemory _memory;

    public FieldSeparatorsTests() => _memory = new EvaluationMemory(_governor);

    public void Dispose()
    {
        _memory.Finish(retainVariables: false);
        Assert.Equal(0, _governor.ReservedBytes);
    }

    private FieldSeparators Prepare(Variables variables)
    {
        var selected = FieldSeparators.FromVariables(variables);
        return selected.PrepareCharacters(variables, _memory, CancellationToken.None)
            .PrepareBytes(variables, _memory, CancellationToken.None);
    }

    [Fact]
    public void UnsplitWordsAndJoinsDoNotBuildMembershipIndexes()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText("é:")));
        var separators = FieldSeparators.FromVariables(variables);
        Assert.False(separators.IsEmpty);
        Assert.Equal(ShellValue.FromText("é"), separators.First);

        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
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
        var prepared = shared.PrepareCharacters(clone, _memory, CancellationToken.None);
        Assert.NotSame(shared, prepared);
        Assert.True(prepared.Contains(new Rune('é')));
        var characterIndex = characters.GetValue(prepared);
        Assert.NotNull(characterIndex);
        Assert.Null(characters.GetValue(separators));
        Assert.Null(bytes.GetValue(prepared));
        Assert.Same(separators, FieldSeparators.FromVariables(variables));
        Assert.Same(prepared, FieldSeparators.FromVariables(clone));

        var completedClone = clone.Clone();
        Assert.Same(prepared, FieldSeparators.FromVariables(completedClone));
        var both = prepared.PrepareBytes(completedClone, _memory, CancellationToken.None);
        Assert.NotSame(prepared, both);
        Assert.True(both.Contains((byte)0xA9));
        Assert.True(both.Contains((byte)':'));
        Assert.NotNull(bytes.GetValue(both));
        Assert.Null(bytes.GetValue(prepared));
        Assert.Same(characterIndex, characters.GetValue(both));
        Assert.Same(prepared, FieldSeparators.FromVariables(clone));
    }

    [Fact]
    public void AggregateJoinsObserveEachSeparatorAndSplittingUsesTheFinalValue()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("separator", ShellValue.FromText(":")));
        Assert.True(variables.TrySetNameref("IFS", ShellValue.FromText("separator")));
        var builder = new ExpandedWordBuilder(variables, _memory, ExpansionPurpose.Normal, ImplicitEmptyPolicy.Remove);

        builder.AppendFields([ShellValue.FromText("left"), ShellValue.FromText("right")],
            allowWordSplitting: true, globQuoted: false, ExpansionQuoteContext.Unquoted);
        builder.AppendValue(ShellValue.FromText("|"), globQuoted: true);
        Assert.True(variables.TrySetScalar("separator", ShellValue.FromText(",")));
        builder.AppendFields([ShellValue.FromText("up"), ShellValue.FromText("down")],
            allowWordSplitting: true, globQuoted: false, ExpansionQuoteContext.Unquoted);

        Assert.Equal([ShellValue.FromText("left:right|up"), ShellValue.FromText("down")],
            builder.Build().Select(word => word.ToValue(_memory)));
    }

    [Theory]
    [InlineData("3A7879", "3A")]
    [InlineData("FF3A", "FF")]
    [InlineData("C2A73A", "C2A7")]
    [InlineData("F09F99823A", "F09F9982")]
    public void CompactFirstSeparatorCanBeRetainedAcrossIfsChanges(string ifsHex, string firstHex)
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromBytes(Convert.FromHexString(ifsHex))));
        var original = FieldSeparators.FromVariables(variables);
        var first = original.CompactFirst;
        Assert.Equal(ShellValue.FromBytes(Convert.FromHexString(firstHex)), first);
        Assert.True(first.TryGetCompactArrayCapacity(out var capacity));
        Assert.Equal(first.Bytes.Length, capacity);
        Assert.True(first.Bytes.Equals(original.CompactFirst.Bytes));
        Assert.True(first.Bytes.Equals(Prepare(variables.Clone()).CompactFirst.Bytes));

        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(",:")));
        Assert.Equal(ShellValue.FromText(","), FieldSeparators.FromVariables(variables).CompactFirst);
        Assert.True(first.Bytes.Equals(original.CompactFirst.Bytes));
    }

    [Fact]
    public void CaseQuoteDelimitersFollowTheSelectedIfsSnapshot()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("separators", ShellValue.FromText("long\u0001")));
        Assert.True(variables.TrySetNameref("IFS", ShellValue.FromText("separators")));
        var original = FieldSeparators.FromVariables(variables);
        Assert.Equal(CaseQuoteBytes.Escape, original.CaseQuoteDelimiters);
        Assert.Equal(0, _governor.ReservedBytes);

        var child = variables.Clone();
        Assert.Same(original, FieldSeparators.FromVariables(child));
        Assert.Equal(CaseQuoteBytes.Escape, Prepare(child).CaseQuoteDelimiters);
        Assert.True(child.TrySetScalar("separators", ShellValue.FromBytes(new byte[] { 0xff, 0x7f, 0x01 })));
        Assert.Equal(CaseQuoteBytes.Escape | CaseQuoteBytes.Null, FieldSeparators.FromVariables(child).CaseQuoteDelimiters);
        Assert.True(child.TrySetScalar("separators", ShellValue.FromText("abc")));
        Assert.Equal(CaseQuoteBytes.None, FieldSeparators.FromVariables(child).CaseQuoteDelimiters);
        Assert.Same(original, FieldSeparators.FromVariables(variables));
        Assert.Equal(CaseQuoteBytes.Escape, original.CaseQuoteDelimiters);
    }

    [Fact]
    public void AHereDocumentJoinRetainsItsSelectedSeparatorDuringFieldEnumeration()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
        var builder = new ExpandedWordBuilder(variables, _memory, ExpansionPurpose.Scalar, ImplicitEmptyPolicy.Preserve);

        Assert.True(builder.TryAppendSelectedFields(ParameterFieldJoin.IfsOrSpace, GetFields(),
            allowWordSplitting: false, globQuoted: true, ExpansionQuoteContext.HereDocument));

        Assert.Equal(ShellValue.FromText("left:right"), Assert.Single(builder.Build()).ToValue(_memory));
        Assert.Equal(ShellValue.FromText(","), FieldSeparators.FromVariables(variables).First);

        IEnumerable<ShellValue> GetFields()
        {
            yield return ShellValue.FromText("left");
            Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(",")));
            yield return ShellValue.FromText("right");
        }
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
            var memory = new EvaluationMemory(_governor);
            try
            {
                var shared = FieldSeparators.FromVariables(clone);
                Assert.Same(separators, shared);
                var compact = shared.CompactFirst;
                var prepared = shared.PrepareCharacters(clone, memory, CancellationToken.None)
                    .PrepareBytes(clone, memory, CancellationToken.None);
                Assert.NotSame(shared, prepared);
                Assert.Same(prepared, FieldSeparators.FromVariables(clone));
                Assert.True(compact.Bytes.Equals(prepared.CompactFirst.Bytes));
                Assert.True(prepared.Contains(new Rune('é')));
                Assert.False(prepared.Contains(Rune.ReplacementChar));
                Assert.True(prepared.Contains((byte)0xFF));
                Assert.True(prepared.Contains((byte)0xA9));
                Assert.False(prepared.Contains((byte)':'));
            }
            finally { memory.Finish(retainVariables: false); }
        })));
        Assert.Same(separators, FieldSeparators.FromVariables(variables));
        Assert.Equal(0, _governor.ReservedBytes);
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
        var defaults = Prepare(variables);
        Assert.True(defaults.Contains(new Rune(' ')));

        Assert.True(variables.TrySetScalar("IFS", ShellValue.Empty));
        var empty = Prepare(variables);
        Assert.True(empty.IsEmpty);
        Assert.Same(empty, Prepare(variables));
        Assert.False(empty.Contains(new Rune(' ')));

        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
        var colon = Prepare(variables);
        Assert.True(colon.Contains(new Rune(':')));
        Assert.False(colon.Contains(new Rune(' ')));
        Assert.True(variables.TryUnset("IFS", UnsetFlags.None));
        Assert.Same(defaults, Prepare(variables));

        Assert.True(variables.TryAssignArray("IFS", [ShellValue.FromText(",")], ArrayKind.Indexed));
        // The existing scalar-only IFS contract uses defaults for array bindings.
        Assert.Same(defaults, Prepare(variables));
        Assert.True(colon.Contains(new Rune(':')));
    }

    [Fact]
    public void CacheTracksNamerefTargetChangesAndCycles()
    {
        var variables = Variables.CreateEmpty();
        var defaults = Prepare(variables);
        Assert.True(variables.TrySetScalar("separator", ShellValue.FromText(",")));
        Assert.True(variables.TrySetNameref("IFS", ShellValue.FromText("separator")));
        var comma = Prepare(variables);
        Assert.True(comma.Contains(new Rune(',')));

        Assert.True(variables.TrySetScalar("separator", ShellValue.FromText(":")));
        var colon = Prepare(variables);
        Assert.NotSame(comma, colon);
        Assert.True(colon.Contains(new Rune(':')));
        Assert.False(colon.Contains(new Rune(',')));

        Assert.True(variables.TrySetNameref("IFS", ShellValue.FromText("other")));
        Assert.Same(defaults, Prepare(variables));
        Assert.True(variables.TrySetScalar("other", ShellValue.Empty));
        Assert.True(Prepare(variables).IsEmpty);
        Assert.True(variables.TryUnset("other", UnsetFlags.None));
        Assert.True(variables.TrySetNameref("other", ShellValue.FromText("IFS")));
        Assert.Same(defaults, Prepare(variables));
    }

    [Fact]
    public void ClonesShareImmutableMembershipButTrackTheirOwnBindings()
    {
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
        var colon = Prepare(variables);
        var clone = variables.Clone();
        Assert.Same(colon, Prepare(clone));

        Assert.True(clone.TrySetScalar("IFS", ShellValue.FromText(",")));
        var comma = Prepare(clone);
        Assert.NotSame(colon, comma);
        Assert.True(comma.Contains(new Rune(',')));
        Assert.Same(colon, Prepare(variables));

        variables.CopyBindingFrom(clone, "IFS");
        var copied = Prepare(variables);
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
        var comma = Prepare(variables);
        Assert.True(variables.TrySetScalar("unrelated", ShellValue.FromText("changed")));
        Assert.Same(comma, Prepare(variables));
        Assert.True(variables.TrySetScalar("IFS", commaValue));
        Assert.Same(comma, Prepare(variables));

        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromBytes(bytes.AsMemory(1, 1))));
        var colon = Prepare(variables);
        Assert.NotSame(comma, colon);
        Assert.True(colon.Contains(new Rune(':')));
        Assert.False(colon.Contains(new Rune(',')));
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
        Assert.NotSame(colon, Prepare(variables));
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
        var separators = Prepare(variables);

        Assert.Equal(hasScalar, separators.Contains(new Rune(scalar)));
        Assert.Equal(hasByte, separators.Contains(rawByte));
        Assert.True(value.Bytes.Equals(separators.Bytes));
        Assert.Same(separators, Prepare(variables));
    }
}
