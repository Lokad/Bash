using System.Diagnostics;

namespace Lokad.Bash.Benchmarks;

internal sealed record ProcessOutput(int ExitCode, ContentDigest Stdout, ContentDigest Stderr);

/// <summary>The same startup and bounded output transport for native and managed child processes.</summary>
internal static class ProcessTransport
{
    public static async Task<ProcessOutput> RunAsync(ProcessStartInfo start, ReadOnlyMemory<byte> input,
        bool waitForWslTimeout, CancellationToken token)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Child process did not start.");
        using var stdout = new Digest();
        using var stderr = new Digest();
        var output = DrainAsync(process.StandardOutput.BaseStream, stdout, token);
        var error = DrainAsync(process.StandardError.BaseStream, stderr, token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (waitForWslTimeout && process.ExitCode is 124 or 137)
                throw new TimeoutException("WSL correctness transport timed out; statuses 124/137 are reserved by its wrapper.");
            return new ProcessOutput(process.ExitCode, stdout.Snapshot(), stderr.Snapshot());
        }
        catch
        {
            // Killing a WSL launcher does not establish termination of Linux users.
            if (!process.HasExited && !waitForWslTimeout) process.Kill(entireProcessTree: true);
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
}
