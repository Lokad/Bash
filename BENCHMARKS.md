# Benchmarks

**The VM throughput campaign is in progress; its complete comparative report is
pending. Keep timing campaigns off the shared development machine.** The separate
cold memory and synchronous core passes are complete. Timings require a quiet VM
and independent measured launches.

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
dotnet run -c Release -f net11.0 --project benchmarks/Lokad.Bash.Benchmarks -- build-check artifacts/benchmarks/prepared
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

Run `build-check DIRECTORY` after preparation on each runtime. It generates and
compiles the five distinct BenchmarkDotNet wrapper types, validates the toolchain,
and checks every copied assembly against the qualified binary's hash. It invokes
neither an executor nor a timing runner. Reports under `builds/` retain generated
code/project files, asset hashes, build logs and completion status. CI exercises
this path on Windows/Linux and .NET 10/.NET 11.

Generated wrappers reference the existing Release binaries directly. This keeps
the benchmark driver, interpreter and dependencies identical to the binaries used
for correctness qualification, including their PE/PDB identity. The driver keeps
its BenchmarkDotNet dependency private. Metadata discovery reads validated case
selection; dispatch and actual benchmark setup still require complete correctness
evidence and the quiet-VM declaration. Build checks confirm all five setup methods
reject their declaration-free compilation contexts.

The pinned BenchmarkDotNet predates a named .NET 11 runtime enum. Its version-enum
SDK/build checks cannot handle `CreateForNewVersion`. The .NET 11 toolchain checks
the actual SDK/target framework and delegates compilation to the pinned SDK,
while retaining the declared .NET 11 runtime identity. .NET 10 uses its known
runtime identity and normal SDK validation. Sequential wrapper compilation avoids
the old version-enum lookup in automatic build scheduling. Both compile-only
paths are checked; independent measured launches remain separate.

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
   above. Install the SDK from `global.json` and the .NET and ASP.NET Core runtimes
   for both .NET 10 and .NET 11. The source test host uses the ASP.NET Core shared
   framework. Supply the GNU binary pinned for that OS in the conformance catalog. A binary or locale
   change requires deliberate requalification; an unpinned system Bash is rejected.
2. Check out the intended commit in a clean checkout, restore with `--locked-mode`,
   build Release, and run `dotnet test Lokad.Bash.slnx -c Release --no-build
   --no-restore`. Retain the command output and SDK/runtime inventory (`dotnet
   --info` and `dotnet --list-runtimes`).
3. Run `prepare` into a fresh directory, such as `artifacts/benchmarks/vm`. Under
   **each** of `net11.0` and `net10.0`, run `verify-managed`, `verify-process`,
   `verify-gates`, `micro-verify`, `build-check` and `verify-native` on that directory. Use a direct GNU binary on
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
   evidence, generated `builds/` reports/logs, and the complete `bdn/` trees containing logs and full JSON results.
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
the archive preserves the exact evidence from that run. The complete throughput
report remains pending.

## Measurement status

The supplied Linux VM is running the qualified campaign on the clean measured
commit `c3139d757334b39c6acc3b701c90b7a9ad239b49`. All timing and memory observations
come from that build. Later documentation edits do not change its identity.
The complete throughput matrix and its cross-family priorities are still pending;
partial results do not establish general GNU Bash parity.

The guest has four AMD EPYC 9V45 vCPUs and runs Ubuntu 24.04.5. Actual runtimes are
.NET `11.0.0-rc.3.26522.148` and `10.0.14`; the former is a preview build. GNU Bash
is the pinned Linux `5.2.21` executable. The default Azure guest power policy is
recorded; frequency controls are unavailable to the guest. Normal Azure/Ubuntu
agents remain, and no physical-host isolation is claimed. A shared lock prevents
overlapping benchmark work; VM access is coordinated with the other project.

### Complete cold memory pass

All 141 runnable catalog cases have one public managed, one prepared managed and
one GNU RSS observation under each runtime's collection driver: 846 observations.
The audit checks source/catalog/qualified binary identities, exact status and output
digests, file effects, counter boundaries and raw Linux RSS/status bytes. These are
single cold invocations, with no spread or steady-state inference.

The following selected .NET 11 prepared observations use the whole-invocation
variant at N=256. Allocation is cumulative process-wide managed bytes during
awaited execution and evaluation cleanup. Admission is the separate coarse peak
ownership estimate, with its wider source/input/host boundary. It excludes CLR and
native overhead and delayed collection. It is not process RSS.

| Family | Declared work per invocation | Allocation (MiB) | Estimated peak admitted (KiB) | Gen0/1/2 collections |
|---|---|---:|---:|---|
| Arithmetic/branch | 256 loop iterations | 3.658 | 36.291 | 0/0/0 |
| Narrow function | 256 calls with three arguments | 4.947 | 36.357 | 0/0/0 |
| Wide function | 16 calls with 256 arguments | 2.388 | 70.519 | 0/0/0 |
| Large prefix environment | Initialize 4,096 bindings, then 256 prefixed commands | 63.828 | 800.611 | 3/1/0 |
| Sparse windows | Build 256 entries, then select 256 late one-element windows | 10.127 | 93.868 | 0/0/0 |
| Pipeline | 16,384 five-byte records through the bounded managed host pipe | 482.499 | 143.736 | 30/1/0 |

For example, the longer arithmetic/branch variant performs 1,024 declared rounds
at N=256. Its prepared cold pass allocates 3,729.988 MiB cumulatively, records
233/1/0 collections, and has estimated peak admission of 37.573 KiB. The rounds
add their own function/loop wrapper and retain shell state; dividing this result
by the repetition count does not reproduce independent whole invocations.

GNU RSS comes from separate GNU time invocations including startup and parsing.
For example, the whole large-prefix case above has native high-water RSS of
4,352 KiB. This does not measure managed allocation or admission and does not
describe summed simultaneous process-tree residency. Managed in-memory input,
pipe and file workflows retain their declared host differences from native OS
workflows; equal script results do not remove that boundary difference.

The full cold-memory tables, per-scale observations and separate plots are retained
in the local review checkpoint `vm-completed-20261113T123846Z-review.zip`, SHA-256
`791c7e155b7c1b5ba3594728db1a0ea781f01d73a13ce548c665248bfb0f2a80`.
Its 1,075 file hashes were verified, including raw completed result metadata and
the exact analysis/plot generators. This checkpoint is not the full prepared
directory archive; the complete invocation, fixture and build trees remain on
the VM for final collection. Generated evidence stays outside Git.

The allocation totals identify useful workloads for investigation. Source review
finds per-evaluation arithmetic parser/work-pump objects and per-call function
local-binding containers as candidates. These observations do not assign a
percentage of time or allocation to either path. The core controls below identify
further candidates; the remaining macro families must inform priorities before
implementation changes.

### Complete synchronous core pass

All 12 core kinds have completed at N=16, 64 and 256 on both runtimes: 72 timing
rows, each with three independent launches and automatic BenchmarkDotNet warmup
and iterations. Each timing below is the median of the three launch medians.
Observed launch ranges and full per-scale data remain in the retained exports;
those ranges are not confidence intervals.

Fixture construction, source/key preparation and exact verification are outside
the kernel. Declared loops, kind dispatch and result consumption remain included.
The synchronous callbacks must complete immediately. Allocation is the reported
BenchmarkDotNet caller-thread counter per kernel, with no allocation-spread
estimate here. It has a different boundary from the process-wide cold async pass.
These kernels have no host I/O or GNU comparison.

The following are complete kernel invocations at N=256. Reported allocation is
identical on the two runtimes for these rows.

| Kernel | Measured work per invocation | .NET 11 (us) | .NET 10 (us) | Reported bytes/kernel |
|---|---|---:|---:|---:|
| ArithmeticPrepared | One expression with 256 binding terms; lexical metadata prepared | 40.490 | 42.429 | 90,736 |
| ArithmeticValue | The same expression as a value operand | 46.503 | 49.193 | 98,960 |
| GlobAdverse | One full no-match over 32,768 ASCII bytes | 349.223 | 350.138 | 0 |
| GlobBytes | One full match over 256 malformed bytes and a final `z` | 2.372 | 2.462 | 0 |
| GlobUtf8 | One full match over 512 Unicode scalars / 1,536 UTF-8 bytes | 13.364 | 13.438 | 0 |
| Literal | 256 quoted literal expansions, each with one command workspace | 13.093 | 12.913 | 69,632 |
| OperationDispatch | 256 operations with immediately completed synthetic callbacks | 6.165 | 6.210 | 40,960 |
| Parameter | 256 quoted parameter expansions, each with one command workspace | 257.406 | 258.501 | 649,216 |
| PositionalFrame | 256 three-argument callee installations/restorations; 4,096 caller arguments | 5.152 | 5.350 | 40,960 |
| Shift | One 256-argument frame installation, 256 drops, and caller restoration | 0.978 | 0.989 | 4,160 |
| SparseWindow | 256 last one-element windows in 16,384 prepared sparse entries | 16.519 | 17.995 | 118,784 |
| VariableLookup | 256 reads of the last of 4,096 prepared bindings | 11.359 | 11.029 | 0 |

The frame control reports 160 bytes per installation/restoration at all three
scales, with about 20–21 ns per call as caller width grows from 256 to 4,096.
The shift counter includes its one callee installation. Its bytes per declared
drop are 20, 17 and 16.25 at the three scales; they are not isolated allocations
made by the drop operation.

On .NET 11, a last sparse window takes 55.664, 58.528 and 64.527 ns per declared
selection as prepared entries grow from 1,024 to 16,384; reported bytes per window
are 400, 432 and 464. These hot repeated selections exclude array construction.
The direct lookup control repeatedly selects one prepared key, rather than random
keys or a newly constructed environment. Three finite scales do not establish
asymptotic complexity or an old-versus-new library speedup.

The quoted literal and parameter controls return the same six UTF-8 bytes.
Across scales, they report 272 and 2,536 bytes per call respectively. At N=256
their .NET 11 times are 51.146 and 1,005.491 ns per call. This comparison applies
to these prepared words and fixed bindings; it is not a ratio for all parameter
forms or complete scripts. Source review shows the parameter path reaches general
brace preparation, word/result builders, braced-syntax parsing and scalar field
construction. Reusing syntax must still evaluate current bindings, quoting,
array/nameref selection, errors and cancellation.

The arithmetic metadata control uses the same expression and immediate constant
bindings. Prepared metadata reduces work inside that numeric kernel; substantial
per-term allocation remains. It excludes shell operand preparation, the numeric
work pump, writes and host effects. Neither these controls nor the dispatch kernel
can be added or subtracted to assign a percentage of macro cost to a component.

Full glob calls include UTF-8/profile validation. The adverse .NET 11 no-match
case takes 22.755, 92.666 and 349.223 us over 2,048, 8,192 and 32,768 bytes, with
zero reported allocation. The UTF-8 and malformed-byte controls have different
patterns and input units. Full matching does not measure prefix/suffix selection
or repeated replacement search; those retain their macro boundaries.

Complete core tables, all scales and launch ranges, source anchors, cold memory
observations, plots and exact generators are retained in
`vm-completed-20261113T141717Z-review.zip`, SHA-256
`d50aaf645153f01225aa63a4b35062a9d4edd8f27ccceecca806634c8d6017b1`.
All 1,219 file hashes were verified. It is a completed-results checkpoint; the
full prepared directory and remaining macro data still need final collection.
Plots retain their partial-campaign label until that matrix is complete.
