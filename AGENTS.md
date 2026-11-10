# Lokad.Bash

Embeddable, non-interactive Bash for .NET. Keep effects behind `IBashHost`, source
LF-only (CR is data), and shell values byte-preserving. Reuse existing helpers and
keep one evaluator and execution/cleanup path.

Read [C# guidelines](docs/CSHARP.md) before editing C# and
[memory design](docs/MEMORY.md) before changing allocation or ownership.
Preserve the [runtime](docs/RUNTIME.md) and [compatibility](docs/COMPATIBILITY.md) contracts.

Use the pinned SDK: `dotnet test Lokad.Bash.slnx -c Release`.
Add regressions beside existing tests. Keep runtime dependencies empty;
development dependencies are centrally versioned and allowlisted.

Keep examples synthetic; exclude application data, configuration, credentials,
external Git history and generated artifacts. Use `Bash: imperative summary` commits.
