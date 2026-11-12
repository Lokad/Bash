# Benchmark preparation

**Comparative timings are pending. Do not run a timing campaign on the shared
development machine.** P6 prepares and checks the harness; P7 requires a quiet,
dedicated VM and independent measured launches.

The non-packable `benchmarks/Lokad.Bash.Benchmarks` project pins development-only
[BenchmarkDotNet 0.15.8](https://www.nuget.org/packages/BenchmarkDotNet/0.15.8).
The runtime NuGet has no benchmark dependency. The pinned SDK builds .NET 11 as
the primary runtime; .NET 10 remains an explicit compatibility/control target.
Use Release throughout.

## Current correctness modes

```powershell
dotnet restore Lokad.Bash.slnx --locked-mode
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- prepare artifacts/benchmarks/prepared
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-managed artifacts/benchmarks/prepared
dotnet run -c Release -f net10.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-managed artifacts/benchmarks/prepared
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-native artifacts/benchmarks/prepared /usr/bin/bash
# Windows may qualify the pinned Linux reference through WSL:
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-native artifacts/benchmarks/prepared /usr/bin/bash Ubuntu
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- smoke artifacts/benchmarks/prepared arithmetic-branch-64 prepared
```

`prepare` emits a versioned deterministic catalog and exact scripts, stdin,
expected output and fixtures. Each blob carries SHA-256 and byte length; each row
has a scale, seed, named semantic work-unit count, locale, options, expected
status/output and file effects. Work units describe that row's operations, not a
universal interpreter instruction count. Seeded read input uses an explicit
32-bit arithmetic formula, without ambient randomness.

The initial catalog has 22 workload families at scales 16, 64 and 256: arithmetic
and branching; narrow/wide calls; shift; small/large prefix environments; arrays
and sparse windows; IFS/read/mapfile; literals/parameters; pattern removal,
replacement and adverse searches; formatting/raw bytes; substitutions; pipelines;
redirection/source/eval; and intentional failure/errexit. Extended patterns remain
an explicitly excluded row. Exact expected nonzero status is part of the failure
workload; an unexpected nonzero, truncated output or different file set fails
verification.

Managed verification executes public and prepared paths independently and runs
controlled host checks first. GNU verification checks the existing conformance
catalog's binary version/hash pins, sanitizes the environment and uses script
files to avoid argv-size differences. Both compare status, stdout/stderr byte
counts and SHA-256, and the complete final fixture file set. No diagnostic
normalization or timeout-as-success is allowed. WSL is a correctness transport,
and is excluded from comparative process-startup measurements. Its Linux-side
timeout bounds cancellation drain; the driver waits for termination rather than
treating a killed Windows launcher as proof that Linux users have stopped.
Statuses 124/137 are reserved by that correctness wrapper.

Verification exports catalog, harness, interpreter and reference identity with
runtime/OS/architecture, host counts, per-row results and completion status.
Failed or partial evidence is unusable for measurement. Output is digested as it
is consumed; transcripts do not grow in memory. `smoke` executes one selected
parse/public/prepared invocation and exports correctness and optional memory
observations, without collecting timings.

## Host and memory boundaries

The small host supplies synthetic flat in-memory files, byte input/output and
64 KiB of pipe transport blocks. Producer writes wait for free blocks. Consumer
lookahead is separately admitted and can exceed transport capacity, so long
records do not deadlock a full pipe. Descriptor gates last through borrowed-read
disposal; retained copies keep their host owner after descriptor closure. Short
records borrow windows and move survivors only on refill. Cancellation/refusal,
prefetched bytes, pending writes and reader closure have controlled checks. Reader closure is distinct from
write failure. No external utilities or pathname enumeration are supplied.

File snapshots retain immutable byte versions until the invocation ends. This
deliberate host retention, copies, metadata and dispatch belong to host-workflow
costs; native real filesystem behavior is a different lane. Language cases,
byte-I/O cases and file workflows retain separate domain labels. Do not turn an
in-memory-file comparison into a claim about pure interpreter parity. A matched
effect lane needs an explicitly comparable host/utility boundary.

Estimated live admission covers active fixture bytes, host transport/lookahead,
file versions and evaluator storage. Prepared catalog/source storage, CLR/native
overhead and delayed collection are outside that estimate. It is not process RSS
or cumulative managed allocation. Smoke allocation deltas use the process-wide
`GC.GetTotalAllocatedBytes`, include asynchronous continuations and driver result
work, and are labelled as cold single-invocation observations.

[BenchmarkDotNet's memory diagnoser](https://benchmarkdotnet.org/articles/configs/diagnosers.html)
uses a caller-thread allocation counter. Restrict it to synchronous micro/parse
methods; asynchronous execution needs the isolated process-wide allocation pass.
GC collections, live admission and native RSS must remain separate metrics.
Parent process-wrapper allocations must never be reported as GNU Bash allocations.

## Remaining harness work

P6 is still open. Measurement dispatch, isolated core microbenchmarks, matched
fresh-process and startup-amortized lanes, strict reusable verification gates and
the complete VM manifest/export procedure are being implemented. The amortization
metadata already excludes scripts whose EOF or shell-exit boundary cannot be
repeated unchanged. No timings, ratios or performance claims have been produced.
