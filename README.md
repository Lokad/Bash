# Lokad.Bash

An embeddable, non-interactive Bash interpreter for .NET 10 and .NET 11.
Your application controls commands, filesystem queries, files, pipes and resource
admission through `IBashHost`. The runtime has no third-party runtime dependencies
and does not supply a filesystem or process host.

This checkout prepares version 0.2.0, including the public API migrations in
[the changelog](CHANGELOG.md). Build the package locally and point your consumer's
NuGet source to the resulting `artifacts/nuget/` directory; publication is separate.

```powershell
dotnet add package Lokad.Bash --version 0.2.0 --source ./artifacts/nuget
```

```csharp
using Lokad.Bash;
using Lokad.Bash.Evaluation;

IBashHost host = new ApplicationBashHost(); // Application-owned capabilities.
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var result = await BashScript.RunAsync(
    host, "for name in alpha beta; do printf '%s\n' \"$name\"; done",
    EvaluationOptions.Default, cancellation.Token);
Console.WriteLine(result.ExitCode);
```

`ApplicationBashHost` is application code. The test suite's
[small host](tests/Lokad.Bash.Tests/EndToEnd/EndToEndHarness.cs) and
[filesystem fixture](tests/Lokad.Bash.Tests/MockFileSystem.cs) illustrate the
contract. They are test fixtures and are not included in the package.

For syntax inspection, use `Syntax.Parser.TryParse` or
`Helpers.ShellCommandParser.TryParseChain`. For prepared or stepped execution,
construct `Evaluation.Evaluation` and drive it through `Engine`. Keep retained
source bytes immutable and retain their storage through all users.

This is a Bash subset. It deliberately excludes interactive sessions, job control,
OS signals, startup files and ambient machine access. Shell utilities are supplied
by the host. See [compatibility](docs/COMPATIBILITY.md),
[runtime contracts](docs/RUNTIME.md) and [memory design](docs/MEMORY.md) for exact
boundaries. Cancellation and memory admission are cooperative; this library is
not an OS isolation boundary.

## Build

Install the SDK in `global.json` and the .NET 10 runtime. The pinned .NET 11 SDK
is a release candidate; both target frameworks are built and tested explicitly.

```powershell
dotnet restore Lokad.Bash.slnx --locked-mode
dotnet test Lokad.Bash.slnx -c Release --no-restore
dotnet pack src/Lokad.Bash/Lokad.Bash.csproj -c Release --no-restore
./scripts/Test-NuGetPackage.ps1 -PackagePath artifacts/nuget/Lokad.Bash.0.2.0.nupkg
```

Language and evaluator-contract regressions use in-memory `IBashHost` fixtures.
Their small command stubs support language assertions; utility implementations,
storage systems and application command-adapter policies are tested by their hosts.
See [test ownership](docs/TESTING.md) for boundary responsibilities and qualification.

CI runs on Windows and Linux. Packages go to `artifacts/nuget/` as
`Lokad.Bash.0.2.0.nupkg` and `Lokad.Bash.0.2.0.snupkg`. The installed consumer
restores only from the produced package in an isolated feed/cache, checks assembly
hashes and exercises both runtimes through the public API. CI retains its evidence.
Before 1.0, minor versions may change public APIs, with migration notes in the
changelog.

The [benchmarks](https://github.com/Lokad/Bash/blob/master/BENCHMARKS.md)
use separate non-packable driver/probe projects. Preparation, equivalence checks,
lifecycle previews, core checks and isolated allocation/native RSS smoke modes run
without comparative timings. The dedicated-VM campaign is complete: 1,254 timing
rows, 846 separate cold memory observations and explicit startup controls cover
25 macro families and 12 core kernels on both runtimes. The report retains each
family's boundaries, scaling, limitations and subsequent investigation priorities.

## GNU correctness controls

The versioned corpus in `tests/Lokad.Bash.Tests/Conformance/cases.json` has explicit
byte/status expectations, script hashes and pinned GNU reference versions/hashes.
The ordinary test suite checks supported cases and separately declared managed
expectations for known deviations. The native verifier checks every applicable
case's GNU expectation, retaining each deviation's reason. Run it with PowerShell 7:

```powershell
./scripts/Verify-GnuBash.ps1 -ReferencePath /usr/bin/bash
# Windows with the pinned Ubuntu/WSL reference:
./scripts/Verify-GnuBash.ps1 -ReferencePath /usr/bin/bash -WslDistribution Ubuntu
```

The verifier rejects an unexpected reference binary and writes results under
`artifacts/conformance/`. These are correctness controls, not a conformance score
or performance measurements. The MSYS reference covers byte/C-locale controls;
Linux `C.utf8` is the character-semantics reference. Broader compatibility gaps
remain documented in [compatibility](docs/COMPATIBILITY.md).
