using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

// GNU Bash 5.2.21/5.2.37, file-backed C locale: logical here-document framing before execution.
public sealed class HereDocumentContinuationTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var prepared in new[] { false, true })
        {
            // heredoc-parity-False-False-1
            yield return [prepared, "mark(){ printf EXEC; }; cat <<mark\ndata\\\nmark\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174616D61726B0A7C307C7461696C", "", "{}"];
            // heredoc-parity-False-False-2
            yield return [prepared, "mark(){ printf EXEC; }; cat <<mark\ndata\\\\\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-False-False-3
            yield return [prepared, "mark(){ printf EXEC; }; cat <<mark\ndata\\\\\\\nmark\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C6D61726B0A7C307C7461696C", "", "{}"];
            // heredoc-parity-False-False-4
            yield return [prepared, "mark(){ printf EXEC; }; cat <<mark\ndata\\\\\\\\\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C5C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-False-True-1
            yield return [prepared, "mark(){ printf EXEC; }; cat <<-mark\n\tdata\\\n\tmark\nmark\nprintf '|%s|tail' \"$?\"", 0, "64617461096D61726B0A7C307C7461696C", "", "{}"];
            // heredoc-parity-False-True-2
            yield return [prepared, "mark(){ printf EXEC; }; cat <<-mark\n\tdata\\\\\n\tmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-False-True-3
            yield return [prepared, "mark(){ printf EXEC; }; cat <<-mark\n\tdata\\\\\\\n\tmark\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C096D61726B0A7C307C7461696C", "", "{}"];
            // heredoc-parity-False-True-4
            yield return [prepared, "mark(){ printf EXEC; }; cat <<-mark\n\tdata\\\\\\\\\n\tmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C5C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-True-False-1
            yield return [prepared, "mark(){ printf EXEC; }; cat <<'mark'\ndata\\\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-True-False-2
            yield return [prepared, "mark(){ printf EXEC; }; cat <<'mark'\ndata\\\\\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C5C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-True-False-3
            yield return [prepared, "mark(){ printf EXEC; }; cat <<'mark'\ndata\\\\\\\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C5C5C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-True-False-4
            yield return [prepared, "mark(){ printf EXEC; }; cat <<'mark'\ndata\\\\\\\\\nmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C5C5C5C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-True-True-1
            yield return [prepared, "mark(){ printf EXEC; }; cat <<-'mark'\n\tdata\\\n\tmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-True-True-2
            yield return [prepared, "mark(){ printf EXEC; }; cat <<-'mark'\n\tdata\\\\\n\tmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C5C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-True-True-3
            yield return [prepared, "mark(){ printf EXEC; }; cat <<-'mark'\n\tdata\\\\\\\n\tmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C5C5C0A7C307C7461696C", "", "{}"];
            // heredoc-parity-True-True-4
            yield return [prepared, "mark(){ printf EXEC; }; cat <<-'mark'\n\tdata\\\\\\\\\n\tmark\nprintf '|%s|tail' \"$?\"", 0, "646174615C5C5C5C0A7C307C7461696C", "", "{}"];
            // heredoc-formed-delimiter
            yield return [prepared, "cat <<END\nE\\\nND\nprintf '|%s|tail' \"$?\"", 0, "7C307C7461696C", "", "{}"];
            // heredoc-skipped
            yield return [prepared, "mark(){ printf EXEC; }; false && cat <<mark\ndata\\\nmark\nmark\nprintf '|%s|tail' \"$?\"", 0, "7C317C7461696C", "", "{}"];
            // heredoc-substitution
            yield return [prepared, "mark(){ printf EXEC; }; x=$(cat <<mark\ndata\\\nmark\nmark\n); printf '<%s>' \"$x\"", 0, "3C646174616D61726B3E", "", "{}"];
            // heredoc-function
            yield return [prepared, "mark(){ printf EXEC; }; f(){ cat <<mark\ndata\\\nmark\nmark\n}; f; printf '|%s|tail' \"$?\"", 0, "646174616D61726B0A7C307C7461696C", "", "{}"];
            // heredoc-eval
            yield return [prepared, "mark(){ printf EXEC; }; eval $'cat <<mark\\ndata\\\\\\nmark\\nmark\\n'; printf '|%s|tail' \"$?\"", 0, "646174616D61726B0A7C307C7461696C", "", "{}"];
            // heredoc-tabs-joined-delimiter
            yield return [prepared, "cat <<-END\n\tE\\\nND\nprintf '|%s|tail' \"$?\"", 0, "7C307C7461696C", "", "{}"];
            // ordinary-joined-False
            yield return [prepared, "x=VALUE; cat <<END\na\\\nb|$x\nEND\nprintf tail", 0, "61627C56414C55450A7461696C", "", "{}"];
            // nested-single-quotes-False
            yield return [prepared, "cat <<END\n$(printf '%s' 'a\\\nb')\nEND\nprintf tail", 0, "61620A7461696C", "", "{}"];
            // literal-delimiter-prefix-False
            yield return [prepared, "cat <<END\nE\\\nNX\nEND\nprintf tail", 0, "454E580A7461696C", "", "{}"];
            // empty-continued-prefix-False
            yield return [prepared, "cat <<END\n\\\nbody\nEND\nprintf tail", 0, "626F64790A7461696C", "", "{}"];
            // multiple-continuations-False
            yield return [prepared, "cat <<END\na\\\n\\\nb\nEND\nprintf tail", 0, "61620A7461696C", "", "{}"];
            // ordinary-joined-True
            yield return [prepared, "x=VALUE; cat <<'END'\na\\\nb|$x\nEND\nprintf tail", 0, "615C0A627C24780A7461696C", "", "{}"];
            // nested-single-quotes-True
            yield return [prepared, "cat <<'END'\n$(printf '%s' 'a\\\nb')\nEND\nprintf tail", 0, "24287072696E746620272573272027615C0A6227290A7461696C", "", "{}"];
            // literal-delimiter-prefix-True
            yield return [prepared, "cat <<'END'\nE\\\nNX\nEND\nprintf tail", 0, "455C0A4E580A7461696C", "", "{}"];
            // empty-continued-prefix-True
            yield return [prepared, "cat <<'END'\n\\\nbody\nEND\nprintf tail", 0, "5C0A626F64790A7461696C", "", "{}"];
            // multiple-continuations-True
            yield return [prepared, "cat <<'END'\na\\\n\\\nb\nEND\nprintf tail", 0, "615C0A5C0A620A7461696C", "", "{}"];
            // body-parameter-across-line
            yield return [prepared, "x=VALUE; cat <<END\n$\\\nx\nEND\nprintf tail", 0, "56414C55450A7461696C", "", "{}"];
            // body-arithmetic-across-line
            yield return [prepared, "cat <<END\n$((1+\\\n2))\nEND\nprintf tail", 0, "330A7461696C", "", "{}"];
            // joined-tab-only-prefix
            yield return [prepared, "cat <<-END\n\t\\\n\tbody\n\tEND\nprintf tail", 0, "626F64790A7461696C", "", "{}"];
            // joined-tab-delimiter
            yield return [prepared, "cat <<-END\n\t\\\n\tEND\nprintf tail", 0, "7461696C", "", "{}"];
            // no-final-lf
            yield return [prepared, "cat <<END\na\\\nb\nEND", 0, "61620A", "", "{}"];
            // two-pending-bodies
            yield return [prepared, "cat <<ONE <<TWO\na\\\nONE\nONE\nb\\\nTWO\nTWO\nprintf tail", 0, "6254574F0A7461696C", "", "{}"];
            // pipeline-pending-bodies
            yield return [prepared, "cat <<ONE | cat <<TWO\na\\\nONE\nONE\nb\\\nTWO\nTWO\nprintf tail", 0, "6254574F0A7461696C", "", "{}"];
            // public-prior-unit
            yield return [prepared, "printf before\ncat <<END\nE\\\nND\nprintf tail", 0, "6265666F72657461696C", "", "{}"];
            // definition-replay
            yield return [prepared, "f(){ cat <<END\na\\\nEND\nEND\n}; eval \"$(declare -f f)\"; f", 0, "61454E440A", "", "{}"];
            // definition-export
            yield return [prepared, "f(){ cat <<END\na\\\nEND\nEND\n}; export -f f; /usr/bin/bash -c f", 0, "61454E440A", "", "{}"];
            // literal-variable-delimiter
            yield return [prepared, "x=VALUE; cat <<$x\n$x\n$x\nprintf tail", 0, "7461696C", "56414C55453A20636F6D6D616E64206E6F7420666F756E640A", "{}"];
            // literal-variable-delimiter-body
            yield return [prepared, "x=VALUE; cat <<$x\nliteral-$x\n$x\nprintf tail", 0, "6C69746572616C2D56414C55450A7461696C", "", "{}"];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task LogicalDelimiterRecognitionPreservesSourceAndEffects(
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
