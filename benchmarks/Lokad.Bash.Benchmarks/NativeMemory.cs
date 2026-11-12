using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Environments;

namespace Lokad.Bash.Benchmarks;

internal sealed record NativeResource(long MaximumResidentKiB, long MaximumResidentBytes, int ReportedExitCode);

/// <summary>Separate Linux GNU-time high-water observation; never observes the CLR parent or WSL launcher.</summary>
internal static class NativeMemory
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length is not (5 or 6)) throw new ArgumentException("memory-native DIRECTORY CASE_ID BASH_PATH GNU_TIME_PATH [WSL_DISTRIBUTION]");
        var distribution = args.Length == 6 ? args[5] : null;
        if (!OperatingSystem.IsLinux() && distribution == null)
            throw new InvalidOperationException("Native RSS collection uses Linux GNU time; Windows qualification requires an explicit WSL distribution.");
        var directory = Path.GetFullPath(args[1]);
        var catalogPath = Path.Combine(directory, "catalog.json");
        var workload = WorkloadCatalog.Read(catalogPath).Cases.Single(row => row.Id == args[2]);
        var exclusion = workload.NativeExclusion("gnu-linux");
        if (exclusion.Length > 0) throw new InvalidDataException(exclusion);
        var transport = distribution == null ? VerificationTransport.NativeDirect : VerificationTransport.WslCorrectness;
        VerificationGate.Read(VerificationReport.NativePath(directory, "gnu-linux"), catalogPath, transport, false);
        var native = distribution == null ? Path.GetFullPath(args[3]) : args[3];
        var collector = distribution == null ? Path.GetFullPath(args[4]) : args[4];
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var pin = await NativeInvocation.VerifyReferenceAsync(native, distribution, VerificationGate.FindReferenceCatalog(), deadline.Token);
        var identity = await NativeInvocation.IdentifyExecutableAsync(collector, distribution, deadline.Token);
        if (!identity.Version.StartsWith("time (GNU Time)", StringComparison.Ordinal))
            throw new InvalidDataException("RSS collection requires GNU time, identified by its reported version and binary hash.");
        var platform = await NativeInvocation.ReadCommandAsync("/usr/bin/uname", ["-srm"], distribution, deadline.Token);
        if (!platform.StartsWith("Linux ", StringComparison.Ordinal)) throw new InvalidDataException("Native RSS units require a Linux kernel.");
        CheckFormat();
        var root = Path.Combine(directory, "memory", $"Native-net{Environment.Version.Major}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = MeasurementDispatch.IdentifySource();
        var environment = HostEnvironmentInfo.GetInformation();
        var invocation = new NativeInvocation(workload, native, distribution, Path.Combine(root, "child"), collector);
        ProcessOutput? output = null;
        NativeResource? resource = null;
        Blob? raw = null;
        IReadOnlyDictionary<string, ContentDigest>? files = null;
        var complete = false;
        Write();
        try
        {
            output = await invocation.RunAsync(deadline.Token);
            files = invocation.ObserveFiles();
            ManagedInvocation.Verify(workload, output.ExitCode, output.Stdout, output.Stderr, files);
            var path = Path.Combine(root, "child", "native-resource.txt");
            if (new FileInfo(path).Length > 128) throw new InvalidDataException("Unexpected native resource output size.");
            raw = Blob.FromBytes(File.ReadAllBytes(path));
            resource = Parse(raw.Bytes, output.ExitCode);
            complete = true;
        }
        finally { Write(); }
        Console.WriteLine("Linux native RSS/status/output invocation verified. No throughput timings collected. Evidence: " + Path.Combine(root, "result.json"));

        static void CheckFormat()
        {
            if (Parse("32\n1\n"u8, 1) != new NativeResource(32, 32768, 1)) throw new InvalidOperationException("Native resource units changed.");
            foreach (var raw in new[] { "", "0\n0\n", "-1\n0\n", "32\n1\n", "32\n0\nextra", "9223372036854775807\n0\n" })
            {
                var rejected = false;
                try { Parse(Encoding.ASCII.GetBytes(raw), 0); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Invalid native resource result was accepted.");
            }
        }

        void Write() => File.WriteAllBytes(Path.Combine(root, "result.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, complete, throughputTimingsCollected = false, singleColdInvocation = true,
            recordedAt = DateTimeOffset.UtcNow, source, environment, nativePlatform = platform.TrimEnd(),
            transport, reference = pin, collector = identity, resourceFormat = "%M\\n%x", formatChecksPassed = true,
            workload.Id, workload.Locale, workload.Variant, workload.Rounds,
            catalogSha256 = Blob.Hash(File.ReadAllBytes(catalogPath)),
            harnessSha256 = Blob.Hash(File.ReadAllBytes(typeof(NativeMemory).Assembly.Location)),
            output, files, raw, resource,
            scope = "Linux GNU time %M high-water RSS in KiB, including startup/script parsing. It is not summed simultaneous process-tree residency. This separate instrumented invocation verifies exact shell status/output/effects. No parent CLR or Windows/WSL launcher memory is observed. Compare neither this metric nor cooperative live admission with cumulative managed allocation bytes."
        }, WorkloadCatalog.Json));
    }

    internal static NativeResource Parse(ReadOnlySpan<byte> bytes, int expectedExit)
    {
        if (bytes.Length > 128) throw new InvalidDataException("Unexpected native resource output size.");
        var lines = Encoding.ASCII.GetString(bytes).Split('\n');
        if (lines.Length != 3 || lines[2].Length != 0
            || !long.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rss) || rss <= 0 || rss > long.MaxValue / 1024
            || !int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exit) || exit != expectedExit || exit is < 0 or > 255)
            throw new InvalidDataException("Native resource output is missing, malformed, unmeasured or reports a different status.");
        return new(rss, rss * 1024, exit);
    }
}
