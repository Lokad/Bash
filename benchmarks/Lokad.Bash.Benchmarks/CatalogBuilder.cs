using System.Globalization;

namespace Lokad.Bash.Benchmarks;

internal static class CatalogBuilder
{
    public static WorkloadCatalog Create()
    {
        const int seed = 1741;
        var cases = new List<Workload>();
        foreach (var n in new[] { 16, 64, 256 })
        {
            var sum = n * (n - 1L) / 2;
            var oddSum = Enumerable.Range(0, n).Where(i => (i & 1) != 0).Sum();
            Add("arithmetic-branch", WorkloadDomain.Language,
                $"sum=0; for ((i=0;i<{n};i++)); do if ((i&1)); then ((sum+=i)); fi; done; printf '%s' \"$sum\"", Text(oddSum), n, "loop iterations");
            Add("function-narrow", WorkloadDomain.Language,
                $"total=0; f() {{ ((total+=$#)); }}; for ((i=0;i<{n};i++)); do f a b c; done; printf '%s' \"$total\"", Text(3L * n), n, "calls with three arguments");
            var arguments = string.Join(' ', Enumerable.Repeat("x", n));
            Add("function-wide", WorkloadDomain.Language,
                $"total=0; f() {{ ((total+=$#)); }}; for ((i=0;i<16;i++)); do f {arguments}; done; printf '%s' \"$total\"", Text(16L * n), 16, $"calls with {n} arguments");
            Add("shift", WorkloadDomain.Language,
                $"set -- {arguments}; count=0; while (($#)); do shift; ((count+=1)); done; printf '%s' \"$count\"", Text(n), n, "single-argument shifts");
            foreach (var width in new[] { 8, n * 16 })
                Add(width == 8 ? "prefix-small" : "prefix-large", WorkloadDomain.Language,
                    $"for ((i=0;i<{width};i++)); do printf -v \"v$i\" '%s' kept; done; for ((i=0;i<{n};i++)); do TEMP=value :; done; printf '%s' \"$i\"",
                    Text(n), n, $"prefix commands against {width} retained bindings");
            Add("array-aggregation", WorkloadDomain.Language,
                $"a=(); for ((i=0;i<{n};i++)); do a[i]=$i; done; sum=0; for value in \"${{a[@]}}\"; do ((sum+=value)); done; printf '%s' \"$sum\"", Text(sum), n, "array insertions and aggregation terms");
            Add("sparse-window", WorkloadDomain.Language,
                $"a=(); for ((i=0;i<{n};i++)); do a[i*1024]=$i; done; sum=0; for ((i=0;i<{n};i++)); do value=${{a[@]:{(n - 1) * 1024}:1}}; ((sum+=value)); done; printf '%s' \"$sum\"", Text(n * (n - 1L)), n, "late one-element sparse windows");
            var fields = string.Join(':', Enumerable.Repeat("part", n));
            Add("ifs", WorkloadDomain.Language,
                $"IFS=:; value='{fields}'; count=0; for part in $value; do ((count+=1)); done; printf '%s' \"$count\"", Text(n), n, "IFS-separated fields");
            Add("literal-quoted", WorkloadDomain.Language,
                $"count=0; for ((i=0;i<{n};i++)); do : 'a b*' \"quoted\" plain; ((count+=1)); done; printf '%s' \"$count\"", Text(n), 3L * n, "literal argument expansions");
            Add("parameter", WorkloadDomain.Language,
                $"value=abcdefgh; count=0; for ((i=0;i<{n};i++)); do part=${{value:2:4}}; ((count+=${{#part}})); done; printf '%s' \"$count\"", Text(4L * n), n, "parameter slices and length queries");
            var subject = new string('a', n * 64) + "b";
            Add("pattern-prefix-suffix", WorkloadDomain.Language,
                $"value='{subject}'; for ((i=0;i<16;i++)); do left=${{value%%*z}}; right=${{value##z*}}; done; printf '%s:%s' \"${{#left}}\" \"${{#right}}\"", Text($"{subject.Length}:{subject.Length}"), 32, "unmatched prefix/suffix selections");
            Add("pattern-replacement", WorkloadDomain.Language,
                $"value='{new string('a', n)}x'; for ((i=0;i<16;i++)); do result=${{value//a/b}}; done; printf '%s' \"$result\"", Text(new string('b', n) + "x"), 16, "global literal replacements");
            var adverse = new string('a', n * 128);
            Add("pattern-adverse", WorkloadDomain.Language,
                $"value='{adverse}'; result=${{value//a*b/z}}; printf '%s' \"${{#result}}\"", Text(adverse.Length), 1, "adverse no-match search over scale*128 bytes");
            var inputValues = Enumerable.Range(0, n).Select(i => unchecked((uint)(seed + i) * 1664525 + 1013904223) % 1000).ToArray();
            var read = Add("read", WorkloadDomain.ByteIo,
                "count=0; sum=0; while IFS=: read -r key value; do ((count+=1,sum+=value)); done; printf '%s:%s' \"$count\" \"$sum\"", Text($"{n}:{inputValues.Sum(value => (long)value)}"), n, "deterministically seeded LF input records");
            const string inputExclusion = "The script consumes stdin until EOF; repeating in one shell would change that input boundary. Use the whole-invocation lane.";
            Replace(read with { Input = Text(string.Concat(inputValues.Select(value => $"row:{value}\n"))), AmortizationExclusion = inputExclusion });
            var mapfile = Add("mapfile", WorkloadDomain.ByteIo,
                $"mapfile -t values; printf '%s:%s' \"${{#values[@]}}\" \"${{values[{n - 1}]}}\"", Text($"{n}:line-{n - 1}"), n, "mapfile records");
            Replace(mapfile with { Input = Text(string.Concat(Enumerable.Range(0, n).Select(i => $"line-{i}\n"))), AmortizationExclusion = inputExclusion });
            var formatted = string.Concat(Enumerable.Range(0, n).Select(i => $"{i:D8}:x\n"));
            Add("format-output", WorkloadDomain.ByteIo,
                $"for ((i=0;i<{n};i++)); do printf '%08d:%s\\n' \"$i\" x; done", Text(formatted), n, "formatted output records");
            Add("raw-output", WorkloadDomain.ByteIo,
                $"for ((i=0;i<{n};i++)); do printf '%s' $'\\377\\r'; done", Blob.FromBytes(Enumerable.Range(0, n).SelectMany(_ => new byte[] { 0xff, 0x0d }).ToArray()), n, "two-byte malformed/CR output records");
            Add("command-substitution", WorkloadDomain.ByteIo,
                $"sum=0; for ((i=0;i<{n};i++)); do value=$(printf '%s' \"$i\"); ((sum+=value)); done; printf '%s' \"$sum\"", Text(sum), n, "builtin command substitutions");
            Add("pipeline", WorkloadDomain.ByteIo,
                $"for ((i=0;i<{n * 64};i++)); do printf 'data\\n'; done | {{ count=0; while IFS= read -r line; do ((count+=1)); done; printf '%s' \"$count\"; }}", Text(n * 64), n * 64L, "five-byte pipe records (largest scale exceeds pipe capacity)");
            var source = Add("redirection-source-eval", WorkloadDomain.FileWorkflow,
                $"source helper.sh; total=0; for ((i=0;i<{n};i++)); do inc; eval '((total+=2))'; done; printf '%s' \"$total\" >result; IFS= read -r value <result || :; printf '%s' \"$value\"", Text(3L * n), n, "function/eval pairs and one output file");
            Replace(source with
            {
                Files = new Dictionary<string, Blob> { ["helper.sh"] = Text("inc() { ((total+=1)); }\n") },
                Expected = source.RequiredExpected with { Files = new Dictionary<string, Blob> { ["result"] = Text(3L * n) } }
            });
            var failure = Add("failure-errexit", WorkloadDomain.Language,
                $"count=0; for ((i=0;i<{n};i++)); do ((count+=1)); done; trap 'printf \"stop:%s:%s\" \"$?\" \"$count\"' EXIT; set -e; false; printf wrong", Text($"stop:1:{n}"), n, "successful iterations before intentional errexit");
            Replace(failure with { AmortizationExclusion = "The script exits the shell; repetition would change its failure boundary.", Expected = failure.RequiredExpected with { ExitCode = 1 } });

            Workload Add(string family, WorkloadDomain domain, string script, Blob stdout, long units, string unit)
            {
                var workload = new Workload($"{family}-{n}", family, domain, n, seed, units, unit,
                    Text(script), Text(""), new Dictionary<string, Blob>(),
                    new ExpectedResult(0, stdout, Text(""), new Dictionary<string, Blob>()), "", "");
                cases.Add(workload);
                return workload;
            }
            void Replace(Workload workload) => cases[cases.Count - 1] = workload;
        }
        var unsupported = cases[0] with
        {
            Id = "unsupported-extglob", Family = "extglob", Scale = 1, WorkUnits = 0,
            Script = Text("[[ alpha == @(alpha|beta) ]]"),
            Expected = null,
            WorkUnit = "excluded syntax; no execution work",
            AmortizationExclusion = "Extended patterns are unsupported in every execution lane.",
            SkipReason = "Extended shell patterns are explicitly outside the supported language; retained as an excluded catalog row."
        };
        cases.Add(unsupported);
        // Work units differ by family. Keep repetitions fixed across that family's
        // geometric scales, without treating one fork as one arithmetic term.
        var repetitions = cases.GroupBy(workload => workload.Family).ToDictionary(group => group.Key, group =>
        {
            var example = group.First();
            if (example.Family.StartsWith("pattern-", StringComparison.Ordinal)) return 4;
            if (example.Family == "command-substitution") return 8;
            if (example.Family is "format-output" or "raw-output" or "prefix-large") return 128;
            if (example.Domain is WorkloadDomain.ByteIo or WorkloadDomain.FileWorkflow) return 16;
            return 1024;
        }, StringComparer.Ordinal);
        foreach (var original in cases.ToArray())
        {
            var rounds = repetitions[original.Family];
            if (original.SkipReason.Length > 0 || !original.CanAmortize)
            {
                cases.Add(original with { Id = original.Id + "-amortized", Variant = WorkloadVariant.StartupAmortized,
                    Rounds = rounds, WorkUnits = 0, Expected = null,
                    SkipReason = original.SkipReason.Length > 0 ? original.SkipReason : original.AmortizationExclusion });
                continue;
            }
            var script = $"__lokad_workload() {{\n{System.Text.Encoding.UTF8.GetString(original.Script.Bytes)}\n}}\n"
                + $"for ((__lokad_round=0;__lokad_round<{rounds};__lokad_round++)); do __lokad_workload; done";
            var expected = original.RequiredExpected;
            cases.Add(original with { Id = original.Id + "-amortized", Variant = WorkloadVariant.StartupAmortized,
                Rounds = rounds, WorkUnits = checked(original.WorkUnits * rounds), WorkUnit = original.WorkUnit + " across declared rounds",
                Script = Text(script), Expected = expected with { Stdout = Repeat(expected.Stdout, rounds), Stderr = Repeat(expected.Stderr, rounds) } });
        }
        var catalog = new WorkloadCatalog(2, seed, repetitions, ["--noprofile", "--norc", "-o", "pipefail"], cases);
        catalog.Validate();
        return catalog;
    }

    private static Blob Text(long number) => Text(number.ToString(CultureInfo.InvariantCulture));
    private static Blob Text(string text) => Blob.FromText(text);
    private static Blob Repeat(Blob blob, int count)
    {
        var bytes = new byte[checked(blob.Bytes.Length * count)];
        for (var index = 0; index < count; index++) blob.Bytes.CopyTo(bytes, index * blob.Bytes.Length);
        return Blob.FromBytes(bytes);
    }
}
