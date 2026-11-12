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
            yield return [Text("id"), script, Text("stdinHex"), sample.GetProperty("exitCode").GetInt32(),
                Text("stdoutHex"), Text("stderrHex"), sample.GetProperty("files").GetRawText(),
                sample.GetProperty("fileEffects").GetRawText()];

            string Text(string property) => sample.GetProperty(property).GetString()
                ?? throw new InvalidOperationException($"Missing {property} in conformance case.");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SupportedCasesMatchPinnedExpectations(
        string id, string script, string stdinHex, int exitCode, string stdoutHex, string stderrHex,
        string filesJson, string effectsJson)
    {
        await using var host = new TestHost();
        using var files = JsonDocument.Parse(filesJson);
        foreach (var file in files.RootElement.EnumerateArray())
        {
            var path = file.GetProperty("path").GetString() ?? throw new InvalidOperationException("Missing fixture path.");
            var content = Convert.FromHexString(file.GetProperty("contentHex").GetString()
                ?? throw new InvalidOperationException("Missing fixture bytes."));
            var hash = file.GetProperty("sha256").GetString();
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
            Assert.DoesNotContain("..", path.Split('/'));
            Assert.False(Path.IsPathRooted(path));
            host.AddFile("/" + path, content);
        }
        host.SetStandardInputBytes(Convert.FromHexString(stdinHex));
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.True(result.ExitCode == exitCode, $"{id}: expected exit {exitCode}, got {result.ExitCode}");
        Assert.Equal(Convert.FromHexString(stdoutHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(stderrHex), host.StandardError.Content.ToArray());
        using var effects = JsonDocument.Parse(effectsJson);
        foreach (var effect in effects.RootElement.EnumerateArray())
        {
            var path = effect.GetProperty("path").GetString() ?? throw new InvalidOperationException("Missing effect path.");
            var content = Convert.FromHexString(effect.GetProperty("contentHex").GetString()
                ?? throw new InvalidOperationException("Missing expected effect bytes."));
            Assert.Equal(content, host.ReadFileBytes("/" + path));
        }
    }
}
