# Lokad.Bash

An embeddable, non-interactive Bash interpreter for .NET 10 and .NET 11.
Your application controls commands, filesystem queries, files, pipes and resource
admission through `IBashHost`. The runtime has no third-party runtime dependencies
and does not supply a filesystem or process host.

```powershell
dotnet add package Lokad.Bash --version 0.1.0
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
```

Language and evaluator-contract regressions use in-memory `IBashHost` fixtures.
Their small command stubs support language assertions; utility implementations,
storage systems and application command-adapter policies are tested by their hosts.

CI runs on Windows and Linux. Packages go to `artifacts/nuget/` as
`Lokad.Bash.0.1.0.nupkg` and `Lokad.Bash.0.1.0.snupkg`.
Before 1.0, minor versions may change public APIs, with migration notes in the
changelog.

## GNU correctness controls

The versioned corpus in `tests/Lokad.Bash.Tests/Conformance/cases.json` has explicit
byte/status expectations, script hashes and pinned GNU reference versions/hashes.
The ordinary test suite runs its supported cases against the managed interpreter.
Verify the same expectations against GNU Bash using PowerShell 7:

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
