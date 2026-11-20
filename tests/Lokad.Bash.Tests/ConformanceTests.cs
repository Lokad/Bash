using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lokad.Bash.Files;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

/// <summary>Verifies shared GNU expectations and separately declared managed departures from the pinned references.</summary>
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
            if (classification is not ("supported" or "deviation")
                || classification == "deviation" && Text("reason").Length == 0)
                throw new InvalidOperationException("Every deviation needs an explicit reason.");
            var expectation = classification == "deviation" ? sample.GetProperty("managedExpectation") : sample;
            var script = Text("script");
            if (sample.TryGetProperty("stderrNormalization", out var normalization)
                && normalization.GetString() != "bash-command-location")
                throw new InvalidOperationException("Unknown diagnostic normalization.");
            if (!StringComparer.OrdinalIgnoreCase.Equals(Text("scriptSha256"),
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))))
                throw new InvalidOperationException($"Script hash mismatch: {Text("id")}");
            yield return [Text("id"), script, Text("stdinHex"), expectation.GetProperty("exitCode").GetInt32(),
                ExpectedText("stdoutHex"), ExpectedText("stderrHex"),
                sample.GetProperty("files").GetRawText(), expectation.GetProperty("fileEffects").GetRawText(),
                expectation.TryGetProperty("finalEntries", out var entries) ? entries.GetRawText() : "null"];

            string Text(string property) => sample.GetProperty(property).GetString()
                ?? throw new InvalidOperationException($"Missing {property} in conformance case.");
            string ExpectedText(string property) => expectation.GetProperty(property).GetString()
                ?? throw new InvalidOperationException($"Missing managed {property} in conformance case.");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CasesMatchDeclaredExpectations(
        string id, string script, string stdinHex, int exitCode, string stdoutHex, string stderrHex,
        string filesJson, string effectsJson, string finalEntriesJson)
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
        using var entries = JsonDocument.Parse(finalEntriesJson);
        AssertFileState(host, files.RootElement, effects.RootElement, entries.RootElement);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileOracleRejectsUnlistedFilesAndDirectories(bool directory)
    {
        await using var host = new TestHost();
        host.AddFile("/out", "expected");
        if (directory) host.AddDirectory("/unexpected");
        else host.AddFile("/3-", "unintended");
        using var files = JsonDocument.Parse("[]");
        using var effects = JsonDocument.Parse("[{\"path\":\"out\",\"contentHex\":\"6578706563746564\"}]");
        using var entries = JsonDocument.Parse("null");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertFileState(host, files.RootElement, effects.RootElement, entries.RootElement));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("empty")]
    public async Task ExplicitFinalEntriesDescribeRemovalAndCheckEntryKinds(string kind)
    {
        await using var host = new TestHost();
        if (kind != "empty") host.AddDirectory("/kept");
        using var files = JsonDocument.Parse("[{\"path\":\"removed\",\"contentHex\":\"00\"}]");
        using var effects = JsonDocument.Parse("[]");
        using var entries = JsonDocument.Parse(kind == "empty" ? "[]"
            : "[{\"path\":\"kept\",\"kind\":\"" + kind + "\",\"contentHex\":\"\"}]");

        if (kind != "file")
            AssertFileState(host, files.RootElement, effects.RootElement, entries.RootElement);
        else
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
                AssertFileState(host, files.RootElement, effects.RootElement, entries.RootElement));
    }

    [Fact]
    public async Task FileOracleChecksUntouchedFixtureBytesAndImplicitParentDirectories()
    {
        await using var host = new TestHost();
        host.AddFile("/nested/deep/in", new byte[] { 0xff, 0x00 });
        host.AddFile("/out", "new");
        using var files = JsonDocument.Parse("[{\"path\":\"nested/deep/in\",\"contentHex\":\"ff00\"},{\"path\":\"out\",\"contentHex\":\"6f6c64\"}]");
        using var effects = JsonDocument.Parse("[{\"path\":\"out\",\"contentHex\":\"6e6577\"}]");
        using var entries = JsonDocument.Parse("null");
        AssertFileState(host, files.RootElement, effects.RootElement, entries.RootElement);

        host.AddFile("/nested/deep/in", new byte[] { 0xff });
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertFileState(host, files.RootElement, effects.RootElement, entries.RootElement));
    }

    private static void AssertFileState(TestHost host, JsonElement files, JsonElement effects, JsonElement finalEntries)
    {
        // Keep each declared byte-content assertion as well as checking the complete tree.
        foreach (var effect in effects.EnumerateArray())
        {
            var path = effect.GetProperty("path").GetString() ?? throw new InvalidOperationException("Missing effect path.");
            var content = Convert.FromHexString(effect.GetProperty("contentHex").GetString()
                ?? throw new InvalidOperationException("Missing expected effect bytes."));
            Assert.Equal(content, host.ReadFileBytes("/" + path));
        }

        var expected = new SortedDictionary<string, (FilePathKind Kind, string ContentHex)>(StringComparer.Ordinal);
        if (finalEntries.ValueKind == JsonValueKind.Null)
        {
            foreach (var file in files.EnumerateArray()) AddFile(file);
            foreach (var file in effects.EnumerateArray()) AddFile(file);
        }
        else
        {
            foreach (var entry in finalEntries.EnumerateArray())
            {
                var path = ReadPath(entry);
                var kind = entry.GetProperty("kind").GetString() switch
                {
                    "file" => FilePathKind.File,
                    "directory" => FilePathKind.Directory,
                    _ => throw new InvalidOperationException("Unsupported expected entry kind.")
                };
                expected.Add(path, (kind, kind == FilePathKind.File ? ReadContent(entry) : string.Empty));
            }
        }
        Assert.Equal(expected.ToArray(), host.CaptureEntries().ToArray());

        void AddFile(JsonElement file)
        {
            var path = ReadPath(file);
            expected[path] = (FilePathKind.File, ReadContent(file));
            for (var separator = path.LastIndexOf('/'); separator >= 0; separator = path.LastIndexOf('/', separator - 1))
                expected[path[..separator]] = (FilePathKind.Directory, string.Empty);
        }

        static string ReadPath(JsonElement entry)
        {
            var path = entry.GetProperty("path").GetString() ?? throw new InvalidOperationException("Missing entry path.");
            Assert.False(Path.IsPathRooted(path));
            Assert.All(path.Split('/'), part => Assert.False(part is "" or "." or ".." || part.Contains('\\')));
            return path;
        }

        static string ReadContent(JsonElement entry) => Convert.ToHexString(Convert.FromHexString(
            entry.GetProperty("contentHex").GetString() ?? throw new InvalidOperationException("Missing expected bytes.")));
    }
}
