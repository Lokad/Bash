using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

internal sealed record ReferencePin(string Id, string Version, string Sha256);

/// <summary>GNU process startup, script-file parsing and byte transport are one declared boundary.</summary>
internal sealed class NativeInvocation
{
    private readonly ProcessStartInfo _start;
    private readonly string _fixtures;
    private readonly ReadOnlyMemory<byte> _input;
    private readonly bool _wsl;
    public NativeInvocation(Workload workload, string executable, string? distribution, string root)
    {
        _fixtures = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(_fixtures);
        File.WriteAllBytes(Path.Combine(root, "script.bash"), workload.Script.Bytes);
        foreach (var file in workload.Files)
        {
            var path = Path.Combine(_fixtures, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _fixtures);
            File.WriteAllBytes(path, file.Value.Bytes);
        }
        _input = workload.Input.Bytes;
        _wsl = distribution != null;
        _start = Start(_wsl ? "/usr/bin/timeout" : executable, distribution, workload.Locale);
        if (_wsl)
        {
            var deadline = workload.Variant == WorkloadVariant.StartupAmortized ? "170s" : "55s";
            foreach (var argument in new[] { "--kill-after=2s", deadline, executable }) _start.ArgumentList.Add(argument);
        }
        _start.WorkingDirectory = _fixtures;
        // A file avoids Windows argv and Linux per-argument limits for adverse source literals.
        foreach (var option in new[] { "--noprofile", "--norc", "-o", "pipefail", "../script.bash" })
            _start.ArgumentList.Add(option);
    }

    public Task<ProcessOutput> RunAsync(CancellationToken token) =>
        ProcessTransport.RunAsync(_start, _input, _wsl, token);

    public IReadOnlyDictionary<string, ContentDigest> ObserveFiles() =>
        Directory.EnumerateFiles(_fixtures, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(_fixtures, file).Replace('\\', '/'),
            file =>
            {
                using var stream = File.OpenRead(file);
                return new ContentDigest(stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
            }, StringComparer.Ordinal);

    public static async Task<ReferencePin> VerifyReferenceAsync(string executable, string? distribution, string referenceCatalog, CancellationToken token)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(referenceCatalog));
        var id = distribution != null || !OperatingSystem.IsWindows() ? "gnu-linux" : "gnu-msys";
        var pin = catalog.RootElement.GetProperty("references").EnumerateArray()
            .Select(row => new ReferencePin(row.GetProperty("id").GetString() ?? "", row.GetProperty("version").GetString() ?? "", row.GetProperty("sha256").GetString() ?? ""))
            .Single(row => row.Id == id);
        var start = IdentityStart(executable);
        start.ArgumentList.Add("--version");
        var version = (await ReadIdentityAsync(start)).Split('\n')[0].TrimEnd('\r');
        if (version != pin.Version)
            throw new InvalidDataException("GNU Bash version differs from the pinned reference.");
        string hash;
        if (distribution == null)
        {
            using var stream = File.OpenRead(executable);
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        else
        {
            var hashing = IdentityStart("/usr/bin/sha256sum");
            hashing.ArgumentList.Add("--"); hashing.ArgumentList.Add(executable);
            hash = (await ReadIdentityAsync(hashing)).Split(' ')[0];
        }
        if (hash != pin.Sha256) throw new InvalidDataException("GNU Bash hash differs from the pinned reference.");
        return pin;

        ProcessStartInfo IdentityStart(string command)
        {
            var identity = Start(distribution == null ? command : "/usr/bin/timeout", distribution, "C");
            if (distribution != null)
                foreach (var argument in new[] { "--kill-after=2s", "10s", command }) identity.ArgumentList.Add(argument);
            return identity;
        }

        async Task<string> ReadIdentityAsync(ProcessStartInfo identity)
        {
            using var process = Process.Start(identity) ?? throw new InvalidOperationException("Reference identification did not start.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                if (process.ExitCode != 0 || (await error.ConfigureAwait(false)).Length != 0)
                    throw new InvalidDataException("Reference identification failed or timed out.");
                return text;
            }
            catch
            {
                if (!process.HasExited && distribution == null) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
                throw;
            }
        }
    }

    private static ProcessStartInfo Start(string executable, string? distribution, string locale)
    {
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        if (OperatingSystem.IsWindows()) start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (distribution != null)
        {
            start.FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe");
            foreach (var argument in new[] { "--distribution", distribution, "--exec", "/usr/bin/env", "-i",
                    "LC_ALL=" + locale, "LANG=" + locale, "PATH=/usr/bin:/bin", executable })
                start.ArgumentList.Add(argument);
        }
        else
        {
            start.FileName = executable;
            start.Environment["LC_ALL"] = locale;
            start.Environment["LANG"] = locale;
            start.Environment["PATH"] = OperatingSystem.IsWindows() ? @"C:\Program Files\Git\usr\bin;C:\Windows\System32" : "/usr/bin:/bin";
        }
        return start;
    }
}
