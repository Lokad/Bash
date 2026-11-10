using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class BindingNameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BytePrefixComparisonsChargeUncachedText(bool textIsPrefix)
    {
        BindingName text = "é";
        var bytes = BindingName.FromValue(ShellValue.FromBytes(new byte[] { 0xc3, 0xa9, 0xff }));
        var allowance = new AllocationAllowance("test limit");
        allowance.Charge(0, 16 * 1024 * 1024);

        Assert.Throws<ExpansionBudget.LimitExceededException>(() => textIsPrefix
            ? bytes.StartsWith(text, allowance) : text.StartsWith(bytes, allowance));
    }

    [Theory]
    [InlineData("615BFF5D", "615BFF", true)]
    [InlineData("615BFF5D", "615BFE", false)]
    [InlineData("615BC3A95D", "615BC3", true)]
    [InlineData("615BC3A95D", "615BFF", false)]
    public void CachedBytePrefixComparisonsNeedNoCopies(string nameHex, string prefixHex, bool matches)
    {
        var name = BindingName.FromValue(ShellValue.FromBytes(Convert.FromHexString(nameHex)));
        var prefix = BindingName.FromValue(ShellValue.FromBytes(Convert.FromHexString(prefixHex)));
        var allowance = new AllocationAllowance("test limit");
        allowance.Charge(0, 16 * 1024 * 1024);

        Assert.Equal(matches, name.StartsWith(prefix, allowance));
    }

    [Fact]
    public void ByteNamesStayDistinctAcrossCopiesAndExports()
    {
        var spellings = new[] { "615BFF5D", "615BFE5D", "615BEFBFBD5D", "615BC3BF5D" };
        var names = spellings.Select(hex => BindingName.FromValue(ShellValue.FromBytes(Convert.FromHexString(hex)))).ToArray();
        var variables = Variables.CreateInitial(names.ToDictionary(name => name, _ => ShellValue.FromText("outer")), []);
        foreach (var name in names) variables.SetExported(name, true);

        var clone = variables.Clone();
        clone.SetScalarIgnoringReadonly(names[0], ShellValue.FromText("inner"));
        Assert.True(variables.TryGetScalar(names[0], out var original));
        Assert.Equal("outer", original.ToUtf8String());
        clone.CopyBindingFrom(variables, names[0]);

        var exports = clone.BuildExportedEnvironment();
        Assert.Equal(spellings.Length, exports.Count);
        foreach (var spelling in spellings)
        {
            var name = BindingName.FromValue(ShellValue.FromBytes(Convert.FromHexString(spelling)));
            var exported = Assert.Single(exports, entry => entry.Name == name);
            Assert.Equal(spelling, Convert.ToHexString(exported.Name.ToValue().Bytes.Span));
            Assert.Equal("outer", exported.Value.ToUtf8String());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("ordinary")]
    [InlineData("a[é]")]
    public void TextAndByteSpellingsHaveTheSameIdentity(string text)
    {
        BindingName name = text;
        var parsed = BindingName.FromValue(ShellValue.FromText(text));
        Assert.Equal(name, parsed);
        Assert.Equal(name.GetHashCode(), parsed.GetHashCode());
        Assert.Equal(ShellValue.FromText(text), parsed.ToValue());
        if (text.Length == 0) Assert.Equal(BindingName.Empty, parsed);
    }
}
