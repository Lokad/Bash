using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

/// <summary>Runs the same explicit expectations that scripts/Verify-GnuBash.ps1 verifies against pinned GNU binaries.</summary>
public sealed class ConformanceTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var resource = typeof(ConformanceTests).Assembly.GetManifestResourceStream(
            "Lokad.Bash.Tests.Conformance.cases.json") ?? throw new InvalidOperationException("Missing conformance catalog.");
        using var catalog = JsonDocument.Parse(resource);
        if (catalog.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidOperationException("Unsupported conformance catalog schema.");
        foreach (var sample in catalog.RootElement.GetProperty("cases").EnumerateArray())
        {
            var classification = Text("classification");
            if (classification != "supported")
            {
                if (classification != "deviation" || Text("reason").Length == 0)
                    throw new InvalidOperationException("Every deviation needs a reason.");
                continue;
            }
            var script = Text("script");
            if (!StringComparer.OrdinalIgnoreCase.Equals(Text("scriptSha256"),
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))))
                throw new InvalidOperationException($"Script hash mismatch: {Text("id")}");
            if (sample.GetProperty("files").GetArrayLength() != 0)
                throw new InvalidOperationException("File fixtures require an explicit host setup.");
            yield return [Text("id"), script, Text("stdinHex"), sample.GetProperty("exitCode").GetInt32(),
                Text("stdoutHex"), Text("stderrHex")];

            string Text(string property) => sample.GetProperty(property).GetString()
                ?? throw new InvalidOperationException($"Missing {property} in conformance case.");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SupportedCasesMatchPinnedExpectations(
        string id, string script, string stdinHex, int exitCode, string stdoutHex, string stderrHex)
    {
        await using var host = new TestHost();
        host.SetStandardInputBytes(Convert.FromHexString(stdinHex));
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.True(result.ExitCode == exitCode, $"{id}: expected exit {exitCode}, got {result.ExitCode}");
        Assert.Equal(Convert.FromHexString(stdoutHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(stderrHex), host.StandardError.Content.ToArray());
    }
}
