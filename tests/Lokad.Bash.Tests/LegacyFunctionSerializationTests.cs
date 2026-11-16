using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class LegacyFunctionSerializationTests
{
    // Pinned Bash 5.2.21 / 5.2.37. Direct, displayed/replayed and exported/imported
    // calls vary reached syntax independently of declaration acceptance and prior effects.
    public static IEnumerable<object?[]> Functions()
    {
        var cases = new object?[][]
        {
            ["if-direct", "f(){ true || : `if`; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["if-replay", "f(){ true || : `if`; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["if-export", "f(){ true || : `if`; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["if-quoted-direct", "f(){ true || : \"`if`\"; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["if-quoted-replay", "f(){ true || : \"`if`\"; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["if-quoted-export", "f(){ true || : \"`if`\"; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["close-direct", "f(){ true || : `)`; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["close-replay", "f(){ true || : `)`; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["close-export", "f(){ true || : `)`; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["close-quoted-direct", "f(){ true || : \"`)`\"; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["close-quoted-replay", "f(){ true || : \"`)`\"; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["close-quoted-export", "f(){ true || : \"`)`\"; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["quote-direct", "f(){ true || : `'`; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["quote-replay", "f(){ true || : `'`; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["quote-export", "f(){ true || : `'`; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["quote-quoted-direct", "f(){ true || : \"`'`\"; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["quote-quoted-replay", "f(){ true || : \"`'`\"; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["quote-quoted-export", "f(){ true || : \"`'`\"; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["valid-direct", "f(){ true || : `printf child`; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["valid-replay", "f(){ true || : `printf child`; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["valid-export", "f(){ true || : `printf child`; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["valid-quoted-direct", "f(){ true || : \"`printf child`\"; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["valid-quoted-replay", "f(){ true || : \"`printf child`\"; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["valid-quoted-export", "f(){ true || : \"`printf child`\"; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["comment-direct", "f(){ true || : `printf child # tail`; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["comment-replay", "f(){ true || : `printf child # tail`; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["comment-export", "f(){ true || : `printf child # tail`; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["comment-quoted-direct", "f(){ true || : \"`printf child # tail`\"; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["comment-quoted-replay", "f(){ true || : \"`printf child # tail`\"; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["comment-quoted-export", "f(){ true || : \"`printf child # tail`\"; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["subshell-direct", "f(){ true || : `(printf child)`; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["subshell-replay", "f(){ true || : `(printf child)`; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["subshell-export", "f(){ true || : `(printf child)`; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["subshell-quoted-direct", "f(){ true || : \"`(printf child)`\"; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["subshell-quoted-replay", "f(){ true || : \"`(printf child)`\"; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["subshell-quoted-export", "f(){ true || : \"`(printf child)`\"; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["arithmetic-direct", "f(){ true || : `((n=1)); printf child`; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["arithmetic-replay", "f(){ true || : `((n=1)); printf child`; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["arithmetic-export", "f(){ true || : `((n=1)); printf child`; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["arithmetic-quoted-direct", "f(){ true || : \"`((n=1)); printf child`\"; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["arithmetic-quoted-replay", "f(){ true || : \"`((n=1)); printf child`\"; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["arithmetic-quoted-export", "f(){ true || : \"`((n=1)); printf child`\"; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["escape-direct", "f(){ true || : `printf '%s' '\\$x'`; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["escape-replay", "f(){ true || : `printf '%s' '\\$x'`; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["escape-export", "f(){ true || : `printf '%s' '\\$x'`; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["escape-quoted-direct", "f(){ true || : \"`printf '%s' '\\$x'`\"; printf ok; }; f", 0, "6F6B", false, null, "{}"],
            ["escape-quoted-replay", "f(){ true || : \"`printf '%s' '\\$x'`\"; printf ok; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6F6B", false, null, "{}"],
            ["escape-quoted-export", "f(){ true || : \"`printf '%s' '\\$x'`\"; printf ok; }; export -f f; bash -c f", 0, "6F6B", false, null, "{}"],
            ["reached-48", "f(){ printf before; false && : `if`; printf after; }; f", 0, "6265666F72656166746572", false, null, "{}"],
            ["reached-49", "f(){ printf before; false && : `if`; printf after; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6265666F72656166746572", false, null, "{}"],
            ["reached-50", "f(){ printf before; false && : `if`; printf after; }; export -f f; bash -c f", 0, "6265666F72656166746572", false, null, "{}"],
            ["reached-51", "f(){ printf before; true && : `if`; printf after; }; f", 0, "6265666F72656166746572", true, null, "{}"],
            ["reached-52", "f(){ printf before; true && : `if`; printf after; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6265666F72656166746572", true, null, "{}"],
            ["reached-53", "f(){ printf before; true && : `if`; printf after; }; export -f f; bash -c f", 0, "6265666F72656166746572", true, null, "{}"],
            ["reached-54", "f(){ printf before; false && : `printf child`; printf after; }; f", 0, "6265666F72656166746572", false, null, "{}"],
            ["reached-55", "f(){ printf before; false && : `printf child`; printf after; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6265666F72656166746572", false, null, "{}"],
            ["reached-56", "f(){ printf before; false && : `printf child`; printf after; }; export -f f; bash -c f", 0, "6265666F72656166746572", false, null, "{}"],
            ["reached-57", "f(){ printf before; true && : `printf child`; printf after; }; f", 0, "6265666F72656166746572", false, null, "{}"],
            ["reached-58", "f(){ printf before; true && : `printf child`; printf after; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6265666F72656166746572", false, null, "{}"],
            ["reached-59", "f(){ printf before; true && : `printf child`; printf after; }; export -f f; bash -c f", 0, "6265666F72656166746572", false, null, "{}"],
            ["reached-60", "f(){ printf before; false && : `)`; printf after; }; f", 0, "6265666F72656166746572", false, null, "{}"],
            ["reached-61", "f(){ printf before; false && : `)`; printf after; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6265666F72656166746572", false, null, "{}"],
            ["reached-62", "f(){ printf before; false && : `)`; printf after; }; export -f f; bash -c f", 0, "6265666F72656166746572", false, null, "{}"],
            ["reached-63", "f(){ printf before; true && : `)`; printf after; }; f", 0, "6265666F72656166746572", true, null, "{}"],
            ["reached-64", "f(){ printf before; true && : `)`; printf after; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A6265666F72656166746572", true, null, "{}"],
            ["reached-65", "f(){ printf before; true && : `)`; printf after; }; export -f f; bash -c f", 0, "6265666F72656166746572", true, null, "{}"],
            ["source-66", "f(){ true || : `if`; printf ok; }; saved=$(declare -f f); source ./unit", 0, "6F6B", false, "f(){ true || : `if`; printf ok; }\nf\n", "{\"unit\":\"File:6628297B2074727565207C7C203A20606966603B207072696E7466206F6B3B207D0A660A\"}"],
            ["source-67", "f(){ true || : `if`; printf ok; }; saved=$(declare -f f); eval \"$saved\"", 0, "", false, null, "{}"],
            ["source-68", "f(){ true || : `if`; printf ok; }; saved=$(declare -f f); f", 0, "6F6B", false, null, "{}"],
            ["extra-0", "x=outer; export x; f(){ printf '<%s>' `printf '%s' \\`printf child\\``; }; f", 0, "3C6368696C643E", false, null, "{}"],
            ["extra-1", "x=outer; export x; f(){ printf '<%s>' `printf '%s' \\`printf child\\``; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C6368696C643E", false, null, "{}"],
            ["extra-2", "x=outer; export x; f(){ printf '<%s>' `printf '%s' \\`printf child\\``; }; export -f f; bash -c f", 0, "3C6368696C643E", false, null, "{}"],
            ["extra-3", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' \\`printf child\\``\"; }; f", 0, "3C6368696C643E", false, null, "{}"],
            ["extra-4", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' \\`printf child\\``\"; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C6368696C643E", false, null, "{}"],
            ["extra-5", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' \\`printf child\\``\"; }; export -f f; bash -c f", 0, "3C6368696C643E", false, null, "{}"],
            ["extra-6", "x=outer; export x; f(){ printf '<%s>' `printf '%s' '\\\\'`; }; f", 0, "3C5C3E", false, null, "{}"],
            ["extra-7", "x=outer; export x; f(){ printf '<%s>' `printf '%s' '\\\\'`; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C5C3E", false, null, "{}"],
            ["extra-8", "x=outer; export x; f(){ printf '<%s>' `printf '%s' '\\\\'`; }; export -f f; bash -c f", 0, "3C5C3E", false, null, "{}"],
            ["extra-9", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' '\\\\'`\"; }; f", 0, "3C5C3E", false, null, "{}"],
            ["extra-10", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' '\\\\'`\"; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C5C3E", false, null, "{}"],
            ["extra-11", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' '\\\\'`\"; }; export -f f; bash -c f", 0, "3C5C3E", false, null, "{}"],
            ["extra-12", "x=outer; export x; f(){ printf '<%s>' `printf '%s' \\$x`; }; f", 0, "3C6F757465723E", false, null, "{}"],
            ["extra-13", "x=outer; export x; f(){ printf '<%s>' `printf '%s' \\$x`; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C6F757465723E", false, null, "{}"],
            ["extra-14", "x=outer; export x; f(){ printf '<%s>' `printf '%s' \\$x`; }; export -f f; bash -c f", 0, "3C6F757465723E", false, null, "{}"],
            ["extra-15", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' \\$x`\"; }; f", 0, "3C6F757465723E", false, null, "{}"],
            ["extra-16", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' \\$x`\"; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C6F757465723E", false, null, "{}"],
            ["extra-17", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' \\$x`\"; }; export -f f; bash -c f", 0, "3C6F757465723E", false, null, "{}"],
            ["extra-18", "x=outer; export x; f(){ printf '<%s>' `printf child # tail`; }; f", 0, "3C6368696C643E", false, null, "{}"],
            ["extra-19", "x=outer; export x; f(){ printf '<%s>' `printf child # tail`; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C6368696C643E", false, null, "{}"],
            ["extra-20", "x=outer; export x; f(){ printf '<%s>' `printf child # tail`; }; export -f f; bash -c f", 0, "3C6368696C643E", false, null, "{}"],
            ["extra-21", "x=outer; export x; f(){ printf '<%s>' \"`printf child # tail`\"; }; f", 0, "3C6368696C643E", false, null, "{}"],
            ["extra-22", "x=outer; export x; f(){ printf '<%s>' \"`printf child # tail`\"; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C6368696C643E", false, null, "{}"],
            ["extra-23", "x=outer; export x; f(){ printf '<%s>' \"`printf child # tail`\"; }; export -f f; bash -c f", 0, "3C6368696C643E", false, null, "{}"],
            ["extra-24", "x=outer; export x; f(){ printf '<%s>' `printf '%s' $'\\377\\r'`; }; f", 0, "3CFF0D3E", false, null, "{}"],
            ["extra-25", "x=outer; export x; f(){ printf '<%s>' `printf '%s' $'\\377\\r'`; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3CFF0D3E", false, null, "{}"],
            ["extra-26", "x=outer; export x; f(){ printf '<%s>' `printf '%s' $'\\377\\r'`; }; export -f f; bash -c f", 0, "3CFF0D3E", false, null, "{}"],
            ["extra-27", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' $'\\377\\r'`\"; }; f", 0, "3CFF0D3E", false, null, "{}"],
            ["extra-28", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' $'\\377\\r'`\"; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3CFF0D3E", false, null, "{}"],
            ["extra-29", "x=outer; export x; f(){ printf '<%s>' \"`printf '%s' $'\\377\\r'`\"; }; export -f f; bash -c f", 0, "3CFF0D3E", false, null, "{}"],
            ["extra-30", "x=outer; export x; f(){ printf '<%s>' `cat <<END\nbody\nEND\n`; }; f", 0, "3C626F64793E", false, null, "{}"],
            ["extra-31", "x=outer; export x; f(){ printf '<%s>' `cat <<END\nbody\nEND\n`; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C626F64793E", false, null, "{}"],
            ["extra-32", "x=outer; export x; f(){ printf '<%s>' `cat <<END\nbody\nEND\n`; }; export -f f; bash -c f", 0, "3C626F64793E", false, null, "{}"],
            ["extra-33", "x=outer; export x; f(){ printf '<%s>' \"`cat <<END\nbody\nEND\n`\"; }; f", 0, "3C626F64793E", false, null, "{}"],
            ["extra-34", "x=outer; export x; f(){ printf '<%s>' \"`cat <<END\nbody\nEND\n`\"; }; saved=$(declare -f f); unset -f f; eval \"$saved\"; printf 'eval:%s:' \"$?\"; f", 0, "6576616C3A303A3C626F64793E", false, null, "{}"],
            ["extra-35", "x=outer; export x; f(){ printf '<%s>' \"`cat <<END\nbody\nEND\n`\"; }; export -f f; bash -c f", 0, "3C626F64793E", false, null, "{}"],
        };
        foreach (var prepared in new[] { false, true })
        foreach (var sample in cases)
            yield return [prepared, ..sample];
    }

    [Theory]
    [MemberData(nameof(Functions))]
    public async Task DisplayAndExportPreserveLegacyValidationAndEscapes(
        bool prepared, string name, string script, int status, string outputHex,
        bool diagnostic, string? source, string filesJson)
    {
        var memory = new MemoryGovernor(1024 * 1024);
        using var owner = memory.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        if (source != null) host.AddFile("/unit", source);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancel.Token)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, cancel.Token);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(diagnostic, !host.StandardError.Content.IsEmpty);
        if (diagnostic) Assert.Contains("syntax error", host.StandardError.TextContent, StringComparison.Ordinal);
        var files = JsonSerializer.Deserialize<SortedDictionary<string, string>>(filesJson)
            ?? throw new InvalidDataException();
        Assert.Equal(files.ToArray(), host.CaptureEntries()
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Value.Kind + ":" + p.Value.ContentHex)).ToArray());
        Assert.True(host.OpenFileCount == 0, name + ": descriptors must close");
        // End the host's export-cache owner only after child invocations and all I/O settle.
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }
}
