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
dotnet build Lokad.Bash.slnx -c Release --no-restore
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- prepare artifacts/benchmarks/prepared
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-managed artifacts/benchmarks/prepared
dotnet run -c Release -f net10.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-managed artifacts/benchmarks/prepared
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-process artifacts/benchmarks/prepared
dotnet run -c Release -f net10.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-process artifacts/benchmarks/prepared
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-gates artifacts/benchmarks/prepared
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-native artifacts/benchmarks/prepared /usr/bin/bash
# Windows may qualify the pinned Linux reference through WSL:
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-native artifacts/benchmarks/prepared /usr/bin/bash Ubuntu
# The pinned Windows reference supports direct process comparisons:
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- verify-native artifacts/benchmarks/prepared 'C:\Program Files\Git\usr\bin\bash.exe'
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- smoke artifacts/benchmarks/prepared arithmetic-branch-64 prepared
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- memory-managed artifacts/benchmarks/prepared arithmetic-branch-64 public
```

`prepare` emits a versioned deterministic catalog and exact scripts, stdin,
expected output and fixtures. Each blob carries SHA-256 and byte length; each row
has a scale, seed, named semantic work-unit count, locale, options, expected
status/output and file effects. Work units describe that row's operations, not a
universal interpreter instruction count. Seeded read input uses an explicit
32-bit arithmetic formula, without ambient randomness.
After the Release build, add `--no-build --no-restore` before `--` in driver commands
to reuse those binaries throughout qualification.

The catalog has 25 workload families at scales 16, 64 and 256: arithmetic
and branching; narrow/wide calls; shift; small/large prefix environments; arrays
and sparse windows; IFS/read/mapfile; literals/parameters; pattern removal,
replacement and adverse searches; formatting/raw bytes; substitutions; pipelines;
redirection/source/eval; and intentional failure/errexit. Extended patterns remain
an explicitly excluded row. Exact expected nonzero status is part of the failure
workload; an unexpected nonzero, truncated output or different file set fails
verification. Schema 3 includes startup-amortized variants and UTF-8/malformed
controls, giving 141 runnable rows and 11 labelled exclusions. Repetition counts are fixed per family across its
geometric scales, so growth is not hidden by inversely changing repetitions.
Do not equate work units from different families. A function wrapper repeats the
same reset body; its function dispatch, initialization and startup remain included.
EOF-consuming and shell-exit scripts are excluded from this variant with reasons.

UTF-8 substring/length and prefix/suffix/replacement families use pinned Linux
`C.utf8`, including supplementary characters. Twelve whole/amortized rows carry
an explicit MSYS reference exclusion because that pinned reference has no
authoritative supplementary-character locale. Linux qualifies all 141 rows;
MSYS qualifies 129 with 23 labelled exclusions. Malformed-byte matching controls
use `C` and run against both references. Measurement gates require a reference
that actually qualifies every selected row. Windows process comparisons exclude
the Linux-only UTF-8 rows; managed in-process lanes can use their WSL correctness
evidence. Reference exclusions never become invented passing observations.

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
Failed or partial evidence is unusable for measurement. The reusable gate checks
exact row/lane coverage, recorded outcomes, environment, catalog and loaded binary
hashes. Its negative controls reject partial reports, duplicate/missing rows,
false skips and mismatched output. Output is digested as it
is consumed; transcripts do not grow in memory. `smoke` executes one selected
parse/public/prepared invocation and exports correctness and optional memory
observations, without collecting timings.

## Measurement lanes and preview

`plan` validates evidence and BenchmarkDotNet configuration, discovers cases and checks one setup,
execution and cleanup lifecycle, including the empty overhead iteration. It starts
no timing job. Process lanes launch one ordinary correctness invocation:

```powershell
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- plan artifacts/benchmarks/prepared parse arithmetic-branch-64
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- plan artifacts/benchmarks/prepared prepared arithmetic-branch-64
```

| Lane | Included boundary |
| --- | --- |
| `parse` | `Parser.Parse`, returning the AST to BenchmarkDotNet; no execution. |
| `prepared` | Fresh evaluator over warmed parsed syntax; awaited execution, output/result digests and evaluation cleanup. |
| `public` | Complete `BashScript.RunAsync` invocation from source, awaited effects and evaluation cleanup. |
| `managed-process` | Fresh minimal apphost, runtime startup, source/request/input loading, host setup/teardown, output drain and result metadata transport. |
| `native-process` | Direct GNU process startup, script-file parsing, execution and output drain. |

The minimal `Lokad.Bash.Probe` executable shares the host implementation and has
no BenchmarkDotNet dependency. Both process lanes use the same parent output
collector; the child forwards bytes without a second digest. Fresh-process
results include the declared managed wrapper work. No empty-process subtraction
is used to manufacture an interpreter-only cost.

Language cases and formatting/raw-output cases have a matched process boundary:
builtin work and the actual OS stdout pipe. Managed in-memory input/pipeline/file
workflows are separately qualified and excluded from process parity ratios.
Their host strategy remains visible in the catalog. Whole and amortized variants
must remain separate, with family/scale/round counts alongside results.

Source conversion, fixture creation, fresh host/evaluator construction, environment
discovery and unrelated verification happen outside the timed method. The process
lanes necessarily include their child startup and transport; parent file-effect
verification follows timing. Each macro iteration owns fresh state. The
[iteration setup contract](https://benchmarkdotnet.org/articles/samples/IntroSetupCleanupIteration.html)
forces one invocation per iteration; synchronous parse has separate instrumentation.
Asynchronous macro methods do not use the caller-thread memory diagnoser.

Actual `measure` dispatch requires a clean checkout and the supplied quiet VM's
declaration. **Do not run these commands on the shared development machine.**
The declaration records `quietDedicated`, `machineLabel`, `isolation`,
`backgroundLoad` and `powerPolicy`. For example, after provisioning the VM:

```powershell
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- measure artifacts/benchmarks/prepared prepared vm.json arithmetic-branch-64
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- measure artifacts/benchmarks/prepared managed-process vm.json arithmetic-branch-64
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- measure artifacts/benchmarks/prepared native-process vm.json arithmetic-branch-64 /usr/bin/bash
```

Jobs use three independent launches and BenchmarkDotNet's automatic warmup and
iteration selection. .NET 11 is labelled primary; a separately invoked .NET 10 job
is labelled compatibility/control. Manifests retain source commit/cleanliness,
CPU/OS/SDK/runtime information, VM policy, selected/excluded rows, exact workload
hashes, boundaries, full BenchmarkDotNet JSON and copied correctness evidence.
Incomplete or failed benchmark reports never mark a timing run complete.
Run all qualification commands under each runtime before its preview or timing
job. Native evidence uses `gnu-REFERENCE-netMAJOR-verification.json`; qualifying
.NET 10 preserves the independent .NET 11 evidence.

## Isolated core microbenchmarks

`prepare` also writes `micro-catalog.json`; `micro-prepare DIRECTORY` creates it
independently. It contains 36 cases: twelve kinds at scales 16, 64 and 256.
Their exact source, input, pattern and fixture-layout bytes carry lengths and
hashes, alongside counts, seed, expected checksum/value and included boundaries.
Fixture layouts specify deterministic binding names, caller/callee widths and
sparse-index geometry; their materialization belongs to setup.

The cases call existing expansion, variable/frame, arithmetic, UTF-8/byte matcher,
indexed-window and operation APIs directly. They cover literal/parameter expansion,
lookup in wide stores, narrow frames over wide callers, repeated shift, value versus
prepared lexical arithmetic, Unicode and malformed-byte matching, an adverse
no-match scan, late sparse windows and immediately completed operation dispatch.
The benchmark assembly has intentional friend access through assembly metadata;
no production public API is added for measurement.

```powershell
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- micro-verify artifacts/benchmarks/prepared
dotnet run -c Release -f net10.0 --project benchmarks/Lokad.Bash.Benchmarks -- micro-verify artifacts/benchmarks/prepared
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- micro-smoke artifacts/benchmarks/prepared GlobBytes-64
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- micro-plan artifacts/benchmarks/prepared
# Only on the supplied quiet VM, using its actual declaration:
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- micro-measure artifacts/benchmarks/prepared vm.json
```

Verification executes each case twice, checking exact bytes, checksum, restored
frame/array state and final admission refunds. Its gate requires complete, current
coverage and rejects altered observations. The preview checks full discovery,
configuration and a setup/run/cleanup lifecycle without a timing job. Core jobs
use the same runtime separation, three launches, VM/clean-source gates and raw
BenchmarkDotNet export as macros.

Prepared state is reused across these synchronous kernels; temporary positional
changes restore on every call. Task-based adapters must complete inline, or the
case fails. Caller-thread `MemoryDiagnoser` is restricted to that synchronous
boundary. One outer kind dispatch, checksum consumption, declared loops and
completed adapters remain included. Fixture construction, exact verification and
serialization are outside timing. These are core implementation measurements
with no GNU process ratio or ambient-locale claim.

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

`memory-managed` runs exactly one verified public or prepared invocation in the
minimal child, with process-wide allocation and GC counters around awaited
execution and evaluation cleanup. Source/input/host setup, prepared parsing and
evaluator construction, result serialization and final host disposal are outside
the counter boundary. Public invocation includes its own parsing and construction.
Each exported report is labelled cold and single-invocation; it makes no
steady-state claim. Peak admission includes the child's source/input owners and
has a wider boundary than its allocation counter. Parent wrapper allocations and
timings are absent from the report.

`memory-native DIRECTORY CASE_ID BASH_PATH GNU_TIME_PATH [WSL_DISTRIBUTION]`
executes one separate Linux resource pass, checking exact status/output/file effects.
It records GNU time's version string and binary hash, the pinned Bash identity,
Linux kernel/architecture, driver environment, raw `%M\n%x` output and parsed
high-water RSS. Unmeasured/zero, missing, malformed or status-mismatched output
fails. Distribution builds may report a version of `UNKNOWN`; that literal string
is preserved with the identifying executable hash.

```powershell
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- memory-native artifacts/benchmarks/prepared arithmetic-branch-64 /usr/bin/bash /usr/bin/time
# Windows can exercise the actual Linux collector through WSL:
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- memory-native artifacts/benchmarks/prepared arithmetic-branch-64 /usr/bin/bash /usr/bin/time Ubuntu
```

[GNU time `%M`](https://www.gnu.org/software/time/manual/html_node/Memory-Resources.html)
is a process high-water metric. On Linux its units are KiB; child usage does not
represent summed simultaneous residency across a process tree, as described by
[`getrusage`](https://man7.org/linux/man-pages/man2/getrusage.2.html). Startup and
script parsing remain included. This pass observes the actual Linux command,
including when transported through WSL; neither the CLR parent nor the Windows
launcher supplies its memory figure. WSL remains excluded from process timing.
Native Windows RSS collection has no equivalent method in this harness and must
not be inferred from the Linux figures.

## Dedicated VM procedure and export

1. Provision a quiet x64 Windows or Linux VM. Record its CPU allocation, isolation,
   background services and power policy in `vm.json` using the declaration fields
   above. Install the SDK from `global.json` and both .NET runtime majors. Supply
   the GNU binary pinned for that OS in the conformance catalog. A binary or locale
   change requires deliberate requalification; an unpinned system Bash is rejected.
2. Check out the intended commit in a clean checkout, restore with `--locked-mode`,
   build Release, and run `dotnet test Lokad.Bash.slnx -c Release --no-build
   --no-restore`. Retain the command output and SDK/runtime inventory (`dotnet
   --info` and `dotnet --list-runtimes`).
3. Run `prepare` into a fresh directory, such as `artifacts/benchmarks/vm`. Under
   **each** of `net11.0` and `net10.0`, run `verify-managed`, `verify-process`,
   `verify-gates`, `micro-verify` and `verify-native` on that directory. Use a direct GNU binary on
   the VM. Recreate evidence there: development-machine reports deliberately fail
   the environment and binary gates. Keep all reports, including failed attempts.
4. Run `plan` for the intended family and each lane. Inspect the manifest's case
   selection, comparison exclusions, identities and VM policy. Then use `measure`
   with the same selection. Keep process comparisons on the same OS, CPU policy,
   binary versions, options, catalog and host boundary. Run .NET 11 primary and
   .NET 10 control jobs separately; avoid concurrent measurement jobs.
5. Use `micro-plan` to inspect core cases and `micro-measure` for their separate VM
   jobs. Use `memory-managed` as a separate cold allocation/GC pass for selected
   public/prepared cases, and `memory-native` on Linux for native high-water RSS.
   Do not turn either memory observation into a throughput measurement.
6. Retain the whole prepared directory: exact inputs/expectations, all verification
   reports, memory reports, every `measurements/*/manifest.json`, context and copied
   evidence, and the complete `bdn/` trees containing logs and full JSON results.
   Add the SDK inventory, build/test logs and `vm.json`. Archive the directory to a
   destination outside it, for example with PowerShell:

   ```powershell
   Compress-Archive -Path artifacts/benchmarks/vm/* -DestinationPath artifacts/benchmarks/vm-evidence.zip
   Get-FileHash artifacts/benchmarks/vm-evidence.zip -Algorithm SHA256
   ```

Preserve raw measurements when producing per-family/scale medians and spread.
Keep whole invocation, amortized, parse, prepared, public and effectful host rows
distinct. Normalize work only within a declared family and boundary. A failed or
incomplete report contributes no timing result, and reused evidence must be
labelled. The source commit plus pinned dependencies supports reconstruction;
the archive preserves the exact evidence from that run. Timings remain pending.

## Measurement status

Harness qualification and measurement preparation run on the shared machine;
comparative timings require the supplied quiet dedicated VM. No timings, ratios
or performance claims have been produced.
