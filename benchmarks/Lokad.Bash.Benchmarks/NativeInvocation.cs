using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

internal sealed record ReferencePin(string Id, string Version, string Sha256);
internal sealed record NativeOutput(int ExitCode, ContentDigest Stdout, ContentDigest Stderr);

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
            foreach (var argument in new[] { "--kill-after=2s", "55s", executable }) _start.ArgumentList.Add(argument);
        }
        _start.WorkingDirectory = _fixtures;
        // A file avoids Windows argv and Linux per-argument limits for adverse source literals.
        foreach (var option in new[] { "--noprofile", "--norc", "-o", "pipefail", "../script.bash" })
            _start.ArgumentList.Add(option);
    }

    public async Task<NativeOutput> RunAsync(CancellationToken token)
    {
        using var process = Process.Start(_start) ?? throw new InvalidOperationException("GNU Bash did not start.");
        using var stdout = new Digest();
        using var stderr = new Digest();
        var output = DrainAsync(process.StandardOutput.BaseStream, stdout, token);
        var error = DrainAsync(process.StandardError.BaseStream, stderr, token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(_input, token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (_wsl && process.ExitCode is 124 or 137)
                throw new TimeoutException("The WSL correctness transport timed out (statuses 124/137 are reserved by its wrapper).");
            return new NativeOutput(process.ExitCode, stdout.Snapshot(), stderr.Snapshot());
        }
        catch
        {
            // A killed WSL launcher does not establish that Linux users have stopped.
            // Its Linux-side timeout bounds cancellation drain instead.
            if (!process.HasExited && !_wsl) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            throw;
        }

        static async Task DrainAsync(Stream stream, Digest digest, CancellationToken token)
        {
            var buffer = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                digest.Append(buffer.AsSpan(0, length));
        }
    }

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
        var start = Start(executable, distribution, "C");
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Reference did not start.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var version = (await stdout.ConfigureAwait(false)).Split('\n')[0].TrimEnd('\r');
        if (process.ExitCode != 0 || (await stderr.ConfigureAwait(false)).Length != 0 || version != pin.Version)
            throw new InvalidDataException("GNU Bash version differs from the pinned reference.");
        string hash;
        if (distribution == null)
        {
            using var stream = File.OpenRead(executable);
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        else
        {
            var hashing = Start("/usr/bin/sha256sum", distribution, "C");
            hashing.ArgumentList.Add("--"); hashing.ArgumentList.Add(executable);
            using var hasher = Process.Start(hashing) ?? throw new InvalidOperationException("Reference hasher did not start.");
            hasher.StandardInput.Close();
            var result = hasher.StandardOutput.ReadToEndAsync(token);
            var failure = hasher.StandardError.ReadToEndAsync(token);
            await hasher.WaitForExitAsync(token).ConfigureAwait(false);
            hash = (await result.ConfigureAwait(false)).Split(' ')[0];
            if (hasher.ExitCode != 0 || (await failure.ConfigureAwait(false)).Length != 0)
                throw new InvalidDataException("Cannot hash the pinned Linux reference.");
        }
        if (hash != pin.Sha256) throw new InvalidDataException("GNU Bash hash differs from the pinned reference.");
        return pin;
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
