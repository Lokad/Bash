# Test ownership

Tests belong to the boundary whose invariant they assert. Similar scripts can
exercise distinct boundaries: a stepped operation test establishes order and
ownership; an integrated script establishes visible shell behavior. Retain both
when those assertions differ. Avoid parser smoke executions that ignore results,
quota-only assertions and allocation counters attached to a caller thread.

| Invariant | Primary owners in `tests/Lokad.Bash.Tests/` |
| --- | --- |
| Syntax and quote provenance | `ParserTests.cs`, `ParserCommandUnitTests.cs`, `ParserHereDocumentTests.cs`, `AnsiCQuotedWordTests.cs`, `TildePrefixTests.cs` |
| Byte values and expansion | `ByteValuedWordTests.cs`, `ConditionalExpansionTests.cs`, `ArithmeticValueTests.cs`, `ArithmeticSyntaxReuseTests.cs` |
| Arithmetic binding and mutation order | `ArithmeticBindingTests.cs`, `ArithmeticOperandTests.cs`, `ReadTargetBindingTests.cs`, `PrintfTargetBindingTests.cs` |
| Execution, flow and status | `ErrexitTests.cs`, `ExecutionLifetimeTests.cs`, `ListTraversalReuseTests.cs`, `EvaluationTests.PipelineStatus.cs` |
| Scope ownership and memory admission | `PrefixPreparationTests.cs`, `PrefixScopeOwnershipTests.cs`, controlled memory-budget assertions in `EngineTests.cs` |
| Descriptor aliases, delayed users and cleanup | `DescriptorTableTests.cs`, `DescriptorBindingsTests.cs`, stepped `EvaluationTests.cs` and `EngineTests.cs` |
| Descriptor snapshot sharing, encounter order and admission | `DescriptorBindingsTests.cs`, `DescriptorMemoryTests.cs`, synchronous `ComplexityRegressionTests.Redirection.cs` |
| Descriptor reference counts, pending deltas and retained backing | `DescriptorReferenceTests.cs`, independent dictionary models in `DescriptorBindingsTests.cs`, shared `CollectionBacking.cs` array measurements |
| Here-input backpressure, producer lifetime and payload admission | `HereInputLifetimeTests.cs`, byte here-document tests and integrated redirection tests |
| Integrated bytes, effects and pipelines | `EndToEnd/`, including `EvaluationPipelineTests.EndToEnd.cs` |
| Pinned native expectations and explicit departures | `ConformanceTests.cs`, `Conformance/cases.json`, `scripts/Verify-GnuBash.ps1` |

`TildePrefixTests` keeps lexical part boundaries, field bytes and complete trees
separate from its delayed write/close assertions. Native CR-source controls use
the pinned Linux reference: MSYS strips those source bytes. Missing-variable
outcomes, colon-delimited assignment prefixes and numeric directory-stack forms
have separate compatibility controls.

`ConditionalParserTests` owns physical offsets, quote metadata, replay spelling and
structural limits. `ConditionalLexingTests` separates valid comment/regex contexts,
invalid newline positions, complete effects, public/prepared syntax boundaries and
shared admission/refunds. Its export cases reuse ExportTestHost's explicit child
owner. SourceMemoryLifetimeTests and ExportLifetimeTests retain normalized syntax
through definition replacement and pending users/cleanup. Synchronous parser
allocation probes belong to the benchmark/review lane; nesting depth and sibling
count are independent controls, and measured runs must verify target-specific DLLs.

`NamedBracketAtomTests` pins class membership and malformed/quoted atoms without
using the matcher as its oracle. `PatternTraversalTests` separately pins physical
atom offsets, direction and owner admission/refunds; its exhaustive matcher-derived
checks establish traversal equivalence. `ShellBracketTests` checks native-qualified
conditional/case/parameter/path roles and complete effects, using ExportTestHost's
child owner for exported function routes. Scalar class membership, UTF-16 utility
units and pathname width are separate profiles. Malformed literal bracket scans
and valid-member scans on wildcard retries are independent work dimensions.

`FieldSeparatorsTests` separates unresolved descriptors, completed immutable
snapshots, scope publication and character/byte membership. `FieldSeparatorMemoryTests`
checks actual scalar backing capacity, equal-length/different-diversity admission,
refusal/cancellation before publication, child-first preparation, fixed byte storage,
pending-host lifetime, target preservation and the next unread record. Retain the
original tight-allowance witness separately from controls requiring room for
coarse prepaid capacity growth. Native IFS nameref results differ by reference
version; do not present the managed target-lookup profile as agreement with either.

For input builtins, distinguish an unset destination, an empty assignment at
EOF, and a preserved binding after a host read failure. Quote the record
expansions when asserting retained delimiter bytes. Help tests check the
selected destination and the next unread record.

Use the pinned SDK and run `dotnet test Lokad.Bash.slnx -c Release`. CI builds,
tests both target frameworks and packs on Windows and Linux. A focused test run
is useful during an edit; release qualification requires the complete boundary
being claimed. `scripts/Test-NuGetPackage.ps1` restores the public consumer in
`validation/PackageConsumer.cs` from the produced NuGet file, using an isolated
local feed/cache without a project reference. It rejects runtime dependencies,
checks loaded assembly hashes and exports runtime/package/source hashes and
individual probe names. Its `-OutputDirectory` accepts an absolute path or a
path relative to the checkout, defaulting to `artifacts/package-validation`.
CI executes it on both operating systems and runtimes.
The probe checks public invocation, prepared reuse, byte arguments/output, lazy
command units, errexit/EXIT, cancellation, memory refunds and matcher migration.
The isolated consumer also checks that a long accepted pipeline reaches host pipe
refusal without a process-fatal stack overflow. It supplements the source suite's
broader ownership and streaming controls.
The same pipeline has a separate smaller-allowance probe that must refuse syntax
admission before requesting a host pipe; both paths assert immediate refunds.

`EndToEndHarness.RunScriptAsync` prepares the complete script before execution.
Use `TestHost.RunAsync` for public command-unit behavior and its earlier effects
before later syntax failure. The mock `cat` resolves byte-validated text paths
against the invocation directory. Ordinary mock files support truncate, append and
read/write handles with shared file content and per-handle positions. Writes are
visible before close; captured read snapshots retain their original bytes. Fixture
content is retained until the fixture is discarded, independently of runtime-value
admission assertions. Benchmark hosts have their separate ownership controls.

`TestHost` and `ExportTestHost` share child-shell dispatch for script files and
`-c`. The fixture resolves script files against the invocation directory, preserves
source and argument bytes, and assigns `$0` according to the launch form. Both forms
use incremental command units, so effects before a syntax error remain observable.
Child syntax diagnostics use the evaluator's spelling. `ChildShellLaunchTests`
covers launch, failure and descriptor cleanup independently of production parsing tests.

Native correctness controls compare exit status, exact stdout/stderr bytes and
the complete synthetic file tree, including entry names, kinds and file bytes.
Without `finalEntries`, the expected tree combines initial `files` and declared
`fileEffects`, including their implicit parent directories. Optional `finalEntries`
instead describes the complete final tree for removals, renames or empty directories:
each entry has a relative `path`, `kind` (`file` or `directory`) and, for files,
`contentHex`. Declared `fileEffects` are still checked individually. Unexpected
files and directories fail both managed and native verification. Each script and
fixture has a content hash. Only the named
GNU command-location diagnostic prefix may be normalized. Controls that explicitly
discard a diagnostic in-script test status and prior effects; separate diagnostic
tests retain responsibility for the message and active descriptor.

Linux UTF-8 controls use the pinned Linux reference and locale. MSYS exclusions
retain their reasons. Known managed departures carry separate native and managed
expectations: a passing departure row does not establish GNU compatibility.
Reference verification collects correctness evidence and no comparative timings.
Supply a fresh `-OutputPath` when rerunning `Verify-GnuBash.ps1`. The report's
catalog hash identifies the exact bytes read for the invocation.

Keep CPU cancellation, shared allowance/refusal, pending borrowed-write/read/close
cleanup and early-pipe-close controls deterministic. Cancellation requests do not
end storage ownership; assertions must await every pending user and cleanup before
checking refunds. Performance instrumentation belongs to the benchmark lane and
must account for asynchronous continuations separately from live-memory admission.
