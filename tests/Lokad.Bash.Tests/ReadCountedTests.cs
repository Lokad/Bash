using Lokad.Bash.Helpers;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ReadCountedTests
{
    [Theory]
    [InlineData("-rn3", "abcdef\n", "<0><abc><0><def>")]
    [InlineData("-rn3", "ab\ncd\n", "<0><ab><0><cd>")]
    [InlineData("-rn2", "ab\ncd\n", "<0><ab><0><>")]
    [InlineData("-rN3", "ab\ncd\n", "<0><ab\n><0><cd>")]
    [InlineData("-rN2 -n3", "ab\ncd\n", "<0><ab\n><0><cd>")]
    [InlineData("-rN3", "ab", "<1><ab><1><>")]
    [InlineData("-rn0", "abcdef\n", "<0><><0><abcdef>")]
    [InlineData("-rn2 -d ''", "a\0bc\n", "<0><a><0><bc>")]
    [InlineData("-rn2", "a\0bc\n", "<0><ab><0><c>")]
    [InlineData("-n2", "a\\\nbcd\n", "<0><ab><0><cd>")]
    [InlineData("-n2", "a\\\0bc\n", "<0><a><0><bc>")]
    public async Task ReadCountPreservesTheFollowingInput(string options, string data, string expected)
    {
        var script = $"IFS= read {options} value; first=$?; IFS= read -r tail; "
            + """printf '<%s><%s><%s><%s>' "$first" "$value" "$?" "$tail" """;
        var host = EndToEndHarness.BuildTree([]);
        var result = await EndToEndHarness.RunScriptAsync(script, host, ShellValue.FromText(data).Bytes);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("-n6", "<><a:b:c>", "<><a><b><c>")]
    [InlineData("-N6", "<:a:b:c><>", "<:a:b:c>")]
    public async Task ExactCountDisablesScalarAndArrayFieldSplitting(string option, string scalar, string array)
    {
        var script = $"IFS=: read {option} a b <<< ':a:b:c'; printf '<%s><%s>' \"$a\" \"$b\"; "
            + $"IFS=: read {option} -a fields <<< ':a:b:c'; printf '<%s>' \"${{fields[@]}}\"";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(scalar + array, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("-n -1", "-1")]
    [InlineData("-N2147483648", "2147483648")]
    [InlineData("-n --help", "--help")]
    public async Task InvalidCountLeavesTheDescriptorAndBindingUntouched(string options, string operand)
    {
        var script = $"value=seed; read {options} value; first=$?; IFS= read -r tail; "
            + """printf '<%s><%s><%s>' "$first" "$value" "$tail" """;
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]), "abc\n"u8.ToArray());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1><seed><abc>", result.StdOut);
        Assert.Equal($"read: {operand}: invalid number\n", result.StdErr);
    }

    [Fact]
    public async Task ReadCountCarriesEscapeAcrossDescriptorWindows()
    {
        var bytes = new byte[DescriptorReader.BufferSize + 4];
        bytes[DescriptorReader.BufferSize - 1] = (byte)'\\';
        "xyz\n"u8.CopyTo(bytes.AsSpan(DescriptorReader.BufferSize));
        var result = await EndToEndHarness.RunScriptAsync(
            """IFS= read -n1 value; first=$?; IFS= read -r tail; printf '<%s><%s><%s>' "$first" "$value" "$tail" """,
            EndToEndHarness.BuildTree([]), bytes);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0><x><yz>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task ReadCountUsesTheByteBoundaryInsideUtf8()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            """IFS= read -rn1 value; IFS= read -r tail; printf '<%s><%s>' "$value" "$tail" """,
            EndToEndHarness.BuildTree([]), "éx\n"u8.ToArray());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { (byte)'<', 0xc3, (byte)'>', (byte)'<', 0xa9, (byte)'x', (byte)'>' },
            result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.StdErr);
    }
}
