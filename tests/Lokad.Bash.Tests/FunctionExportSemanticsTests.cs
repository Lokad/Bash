using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

// GNU Bash 5.2.21/5.2.37, file-backed C locale: export/import effects, separate from lifetime tests.
public sealed class FunctionExportSemanticsTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var prepared in new[] { false, true })
        {
            // basic
            yield return [prepared, "f(){ printf ok; }; export -f f; /usr/bin/bash -c f", 0, "6F6B", "", "{}"];
            // remove-export-attribute
            yield return [prepared, "f(){ printf forbidden; }; export -f f; /usr/bin/true; export -fn f; /usr/bin/bash -c 'declare -F f; printf \"|%s\" \"$?\"'", 0, "7C31", "", "{}"];
            // readonly-is-parent-only
            yield return [prepared, "f(){ printf parent; }; readonly -f f; export -f f; /usr/bin/bash -c 'f(){ printf child; }; f'; f", 0, "6368696C64706172656E74", "", "{}"];
            // replace-cached
            yield return [prepared, "f(){ printf old; }; export -f f; /usr/bin/true; f(){ printf new; }; /usr/bin/bash -c f; f", 0, "6E65776E6577", "", "{}"];
            // unset-cached
            yield return [prepared, "f(){ printf forbidden; }; export -f f; /usr/bin/true; unset -f f; /usr/bin/bash -c 'declare -F f; printf \"|%s\" \"$?\"'", 0, "7C31", "", "{}"];
            // reexport
            yield return [prepared, "f(){ printf nested; }; export -f f; /usr/bin/bash -c '/usr/bin/bash -c f'", 0, "6E6573746564", "", "{}"];
            // scalar-and-function
            yield return [prepared, "f=scalar; export f; f(){ printf '<%s>' \"$f\"; }; export -f f; /usr/bin/bash -c f", 0, "3C7363616C61723E", "", "{}"];
            // first-pipeline-publication
            yield return [prepared, "f(){ printf parent; }; export -f f; /usr/bin/true | :; /usr/bin/bash -c f; f", 0, "706172656E74706172656E74", "", "{}"];
            // two-pipeline-consumers
            yield return [prepared, "f(){ printf shared; }; export -f f; /usr/bin/true | /usr/bin/true; /usr/bin/bash -c f", 0, "736861726564", "", "{}"];
            // capture-consumer
            yield return [prepared, "f(){ printf capture; }; export -f f; x=$(/usr/bin/bash -c f); printf '<%s>' \"$x\"", 0, "3C636170747572653E", "", "{}"];
            // subshell-replacement
            yield return [prepared, "f(){ printf old; }; export -f f; /usr/bin/true; ( f(){ printf child; }; /usr/bin/bash -c f; unset -f f ); /usr/bin/bash -c f", 0, "6368696C646F6C64", "", "{}"];
            // self-unset
            yield return [prepared, "f(){ unset -f f; printf active; }; export -f f; /usr/bin/bash -c 'f; declare -F f; printf \"|%s\" \"$?\"'; f", 0, "6163746976657C31616374697665", "", "{}"];
            // self-replacement
            yield return [prepared, "f(){ f(){ printf new; }; /usr/bin/bash -c f; printf oldtail; }; export -f f; f; /usr/bin/bash -c f", 0, "6E65776F6C647461696C6E6577", "", "{}"];
            // sourced-definition
            yield return [prepared, "printf '%s\\n' 'f(){ printf sourced; }' >unit; source ./unit; export -f f; /usr/bin/bash -c f; unset -f f", 0, "736F7572636564", "", "{\"unit\":\"File:6628297B207072696E746620736F75726365643B207D0A\"}"];
            // eval-definition
            yield return [prepared, "eval 'f(){ printf evaluated; }'; export -f f; /usr/bin/bash -c f; unset -f f", 0, "6576616C7561746564", "", "{}"];
            // exit-definition
            yield return [prepared, "trap 'f(){ printf trapped; }; export -f f; /usr/bin/bash -c f; unset -f f' EXIT; :", 0, "74726170706564", "", "{}"];
            // legacy-substitution
            yield return [prepared, "f(){ printf '<%s>' `printf legacy`; }; export -f f; /usr/bin/bash -c f", 0, "3C6C65676163793E", "", "{}"];
            // deferred-legacy-syntax
            yield return [prepared, "f(){ : `if`; }; export -f f; /usr/bin/bash -c 'declare -F f'", 0, "660A", "", "{}"];
            // raw-body-bytes
            yield return [prepared, "f(){ printf '%s' $'L\\377R'; }; export -f f; /usr/bin/bash -c f", 0, "4CFF52", "", "{}"];
            // function-redirection
            yield return [prepared, "f(){ printf result; } >out; export -f f; /usr/bin/bash -c f; cat out", 0, "726573756C74", "", "{\"out\":\"File:726573756C74\"}"];
            yield return [prepared, "f(){ { printf O; printf E >&2; } >out |& cat; }; export -f f; /usr/bin/bash -c f; cat out", 0,
                "4F45", "", "{\"out\":\"File:4F45\"}"];
            // clear-environment
            yield return [prepared, "f(){ printf forbidden; }; export -f f; exec -c /usr/bin/bash -c 'declare -F f; printf \"|%s\" \"$?\"'", 0, "7C31", "", "{}"];
            // argument-frame
            yield return [prepared, "f(){ printf '<%s>' \"$0\" \"$@\"; }; export -f f; /usr/bin/bash -c 'f \"a b\" \"\"' tag", 0, "3C7461673E3C6120623E3C3E", "", "{}"];
            // repeated-retirement
            yield return [prepared, "i=0; while ((i<16)); do f(){ printf value; }; export -f f; /usr/bin/true; unset -f f; ((i+=1)); done; printf '%s' \"$i\"", 0, "3136", "", "{}"];
            // attribute-copy-reuse
            yield return [prepared, "f(){ printf same; }; export -f f; /usr/bin/true; export -fn f; export -f f; readonly -f f; /usr/bin/bash -c f", 0, "73616D65", "", "{}"];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExportImportAndRetirementPreserveShellEffects(
        bool prepared, string source, int status, string outputHex, string errorHex, string filesJson)
    {
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new ExportTestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), host.StandardError.Content.ToArray());
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(filesJson)
            ?? throw new InvalidDataException("Missing synthetic expected tree.");
        var actual = host.CaptureEntries().ToDictionary(entry => entry.Key.TrimStart('/'),
            entry => entry.Value.Kind + ":" + entry.Value.ContentHex);
        Assert.Equal(expected.OrderBy(entry => entry.Key), actual.OrderBy(entry => entry.Key));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
