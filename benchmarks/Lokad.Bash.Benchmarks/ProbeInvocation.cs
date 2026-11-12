using System.Diagnostics;
using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

internal sealed record ProbeIdentity(string ExecutableSha256, string AssemblySha256, string RuntimeConfigSha256,
    string DependenciesSha256, string InterpreterSha256);

/// <summary>Prepares files outside the measured fresh-process boundary.</summary>
internal sealed class ProbeInvocation
{
    private readonly ProcessStartInfo _start;
    private readonly string _resultPath;
    public ProbeInvocation(Workload workload, ProbeMode mode, bool allocationCounters, string executable, string root)
    {
        var fixtures = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(fixtures);
        File.WriteAllBytes(Path.Combine(root, "script.bash"), workload.Script.Bytes);
        File.WriteAllBytes(Path.Combine(root, "stdin.bin"), workload.Input.Bytes);
        foreach (var file in workload.Files)
        {
            var path = Path.Combine(fixtures, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? fixtures);
            File.WriteAllBytes(path, file.Value.Bytes);
        }
        var request = new ProbeRequest(mode, checked((int)workload.Script.Length), checked((int)workload.Input.Length),
            workload.Files.ToDictionary(pair => pair.Key, pair => checked((int)pair.Value.Length), StringComparer.Ordinal), allocationCounters);
        File.WriteAllBytes(Path.Combine(root, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request, WorkloadCatalog.Json));
        _resultPath = Path.Combine(root, "result.json");
        _start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixtures };
        _start.Environment.Clear();
        if (OperatingSystem.IsWindows()) _start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { } dotnet) _start.Environment["DOTNET_ROOT"] = dotnet;
        _start.Environment["LC_ALL"] = workload.Locale;
        _start.Environment["LANG"] = workload.Locale;
        foreach (var argument in new[] { "../script.bash", "../request.json", "../result.json" }) _start.ArgumentList.Add(argument);
    }

    public Task<ProcessOutput> RunAsync(CancellationToken token) => ProcessTransport.RunAsync(_start, ReadOnlyMemory<byte>.Empty, false, token);
    public ProbeResult ObserveResult()
    {
        var result = JsonSerializer.Deserialize<ProbeResult>(File.ReadAllBytes(_resultPath), WorkloadCatalog.Json)
            ?? throw new InvalidDataException("Probe did not report completion.");
        if (result.RuntimeMajor != Environment.Version.Major || result.Runtime != System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            || result.Architecture != System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString())
            throw new InvalidDataException("Probe ran on a different runtime or architecture.");
        return result;
    }

    public static string FindExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Lokad.Bash.slnx"))) directory = directory.Parent;
        if (directory == null) throw new InvalidOperationException("Cannot locate the probe checkout.");
        return Path.Combine(directory.FullName, "benchmarks", "Lokad.Bash.Probe", "bin", "Release",
            $"net{Environment.Version.Major}.0", OperatingSystem.IsWindows() ? "Lokad.Bash.Probe.exe" : "Lokad.Bash.Probe");
    }
    public static ProbeIdentity Identify(string executable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable)) ?? throw new ArgumentException("Probe has no directory.");
        var dependencyFile = Path.Combine(directory, "Lokad.Bash.Probe.deps.json");
        using (var deps = JsonDocument.Parse(File.ReadAllBytes(dependencyFile)))
            if (deps.RootElement.GetProperty("libraries").EnumerateObject().Any(library => library.Name.StartsWith("BenchmarkDotNet", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The minimal process probe must not depend on BenchmarkDotNet.");
        var interpreter = Blob.Hash(File.ReadAllBytes(Path.Combine(directory, "Lokad.Bash.dll")));
        if (interpreter != Blob.Hash(File.ReadAllBytes(typeof(BashScript).Assembly.Location)))
            throw new InvalidDataException("Probe and driver load different interpreter assemblies.");
        return new(Blob.Hash(File.ReadAllBytes(executable)),
            Blob.Hash(File.ReadAllBytes(Path.Combine(directory, "Lokad.Bash.Probe.dll"))),
            Blob.Hash(File.ReadAllBytes(Path.Combine(directory, "Lokad.Bash.Probe.runtimeconfig.json"))),
            Blob.Hash(File.ReadAllBytes(dependencyFile)), interpreter);
    }
}
