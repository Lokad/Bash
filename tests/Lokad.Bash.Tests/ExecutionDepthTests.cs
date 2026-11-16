using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Lokad.Bash.Tests;

public sealed class ExecutionDepthTests
{
    internal const int CancellationAdmissionBytes = 2 * 1024 * 1024;

    [Theory]
    [InlineData("recursive", false)]
    [InlineData("recursive", true)]
    [InlineData("chain", false)]
    [InlineData("chain", true)]
    [InlineData("scopes", false)]
    [InlineData("scopes", true)]
    [InlineData("return", false)]
    [InlineData("return", true)]
    [InlineData("yielded", false)]
    [InlineData("yielded", true)]
    [InlineData("eval", false)]
    [InlineData("eval", true)]
    [InlineData("source", false)]
    [InlineData("source", true)]
    public async Task FiniteCallsCompleteAcrossExecutionDepth(string scenario, bool prepared)
    {
        const int Depth = 512;
        var source = scenario switch
        {
            "recursive" => "f(){ ((n-- > 0)) && f; }; n=512; f; printf '%s' \"$n\"",
            "chain" => string.Join('\n', Enumerable.Range(0, Depth).Select(index =>
                "f" + index + "(){ f" + (index + 1) + "; };"))
                + "\nf512(){ :; }; f0; printf after",
            "scopes" => "set -- caller; x=global; f(){ local x=$1; if ((n-- > 0)); then f inner; fi; "
                + "[ \"$x\" = \"$1\" ] || exit 91; }; n=512; f outer; printf '%s:%s:%s' \"$x\" \"$1\" \"$n\"",
            "return" => "f(){ if ((n-- > 0)); then f; else return 7; fi; }; n=512; f; printf '%s:%s' \"$?\" \"$n\"",
            "yielded" => "f(){ printf . >&2; ((n-- > 0)) && f; }; n=512; f; printf '%s' \"$n\"",
            "eval" => "code='if ((n-- > 0)); then eval \"$code\"; fi'; n=512; eval \"$code\"; printf '%s' \"$n\"",
            "source" => "n=512; source /depth.sh; printf '%s' \"$n\"",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var observed = await RunAsync(source, prepared, 32 * 1024 * 1024, 10000, TestCancellation.Deadline);
        var expected = scenario switch
        {
            "chain" => "after",
            "scopes" => "global:caller:-1",
            "return" => "7:-1",
            _ => "-1"
        };

        Assert.False(observed.Canceled);
        Assert.Equal(0, observed.Status);
        Assert.Equal(Convert.ToHexString(Encoding.UTF8.GetBytes(expected)), observed.OutputHex);
        Assert.Equal(scenario == "yielded" ? new string('.', Depth + 1) : "",
            Encoding.UTF8.GetString(Convert.FromHexString(observed.ErrorHex)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationUnwindsDeepCallsWithAnOwnedHereInput(bool prepared)
    {
        var source = "exec 7<<<held; printf before; trap 'printf unwanted' EXIT; f(){ local x=$1; f \"$x\"; }; f '"
            + new string('x', 1024) + "'";
        var observed = await RunAsync(source, prepared, 128 * 1024 * 1024, 10000, TestCancellation.Admission);

        Assert.True(observed.Canceled);
        Assert.Null(observed.Status);
        Assert.Equal(Convert.ToHexString("before"u8), observed.OutputHex);
        Assert.Empty(observed.ErrorHex);
        Assert.True(observed.PeakBytes >= CancellationAdmissionBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusalUnwindsDeepLocalOwnersAndDescriptors(bool prepared)
    {
        var source = "set -e; exec 7<<<held; printf before; f(){ local x=$1; if ((n-- > 0)); then f \"$x\"; fi; }; "
            + "n=512; f '" + new string('x', 1024) + "'; printf unwanted";
        var observed = await RunAsync(source, prepared, 64 * 1024, 10000, TestCancellation.Deadline);

        Assert.False(observed.Canceled);
        Assert.Equal(1, observed.Status);
        Assert.Equal(Convert.ToHexString("before"u8), observed.OutputHex);
        Assert.Contains("shared memory budget exhausted", Encoding.UTF8.GetString(Convert.FromHexString(observed.ErrorHex)));
    }

    private static async Task<TestProcessResult> RunAsync(
        string source, bool prepared, long allowance, int milliseconds, TestCancellation cancellation)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { typeof(ExecutionDepthTests).Assembly.Location,
            "--run-shell", prepared ? "prepared" : "public", source,
            allowance.ToString(CultureInfo.InvariantCulture), milliseconds.ToString(CultureInfo.InvariantCulture), cancellation.ToString() })
            start.ArgumentList.Add(argument);

        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the containment child.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await child.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
        var stderr = await error;
        Assert.True(child.ExitCode == 0, $"Containment child exited {child.ExitCode}: {stderr[..Math.Min(stderr.Length, 2048)]}");
        var result = JsonSerializer.Deserialize<TestProcessResult>(await output)
            ?? throw new InvalidDataException("Missing containment result.");
        Assert.Equal(Environment.Version.Major, result.RuntimeMajor);
        Assert.Equal(0, result.OpenFileCount);
        Assert.Equal(0, result.ReservedBytes);
        return result;
    }
}
