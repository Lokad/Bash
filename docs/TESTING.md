# Test ownership

Tests belong to the boundary whose invariant they assert. Similar scripts can
exercise distinct boundaries: a stepped operation test establishes order and
ownership; an integrated script establishes visible shell behavior. Retain both
when those assertions differ. Avoid parser smoke executions that ignore results,
quota-only assertions and allocation counters attached to a caller thread.

| Invariant | Primary owners in `tests/Lokad.Bash.Tests/` |
| --- | --- |
| Syntax and quote provenance | `ParserTests.cs`, `ParserCommandUnitTests.cs`, `ParserHereDocumentTests.cs`, `AnsiCQuotedWordTests.cs` |
| Byte values and expansion | `ByteValuedWordTests.cs`, `ConditionalExpansionTests.cs`, `ArithmeticValueTests.cs`, `ArithmeticSyntaxReuseTests.cs` |
| Arithmetic binding and mutation order | `ArithmeticBindingTests.cs`, `ArithmeticOperandTests.cs`, `ReadTargetBindingTests.cs`, `PrintfTargetBindingTests.cs` |
| Execution, flow and status | `ErrexitTests.cs`, `ExecutionLifetimeTests.cs`, `ListTraversalReuseTests.cs`, `EvaluationTests.PipelineStatus.cs` |
| Scope ownership and memory admission | `PrefixPreparationTests.cs`, `PrefixScopeOwnershipTests.cs`, controlled memory-budget assertions in `EngineTests.cs` |
| Descriptor aliases, delayed users and cleanup | `DescriptorTableTests.cs`, `DescriptorBindingsTests.cs`, stepped `EvaluationTests.cs` and `EngineTests.cs` |
| Integrated bytes, effects and pipelines | `EndToEnd/`, including `EvaluationPipelineTests.EndToEnd.cs` |
| Pinned native expectations and explicit departures | `ConformanceTests.cs`, `Conformance/cases.json`, `scripts/Verify-GnuBash.ps1` |

Use the pinned SDK and run `dotnet test Lokad.Bash.slnx -c Release`. CI builds,
tests both target frameworks and packs on Windows and Linux. A focused test run
is useful during an edit; release qualification requires the complete boundary
being claimed. `scripts/Test-NuGetPackage.ps1` restores the public consumer in
`validation/PackageConsumer.cs` from the produced NuGet file, using an isolated
local feed/cache without a project reference. It rejects runtime dependencies,
checks loaded assembly hashes and exports runtime/package/source hashes and
individual probe names. CI executes it on both operating systems and runtimes.
The probe checks public invocation, prepared reuse, byte arguments/output, lazy
command units, errexit/EXIT, cancellation, memory refunds and matcher migration.
The isolated consumer also checks that a long accepted pipeline reaches host pipe
refusal without a process-fatal stack overflow. It supplements the source suite's
broader ownership and streaming controls.

Native correctness controls compare exit status, exact stdout/stderr bytes and
declared file effects. Each script and fixture has a content hash. Only the named
GNU command-location diagnostic prefix may be normalized. Controls that explicitly
discard a diagnostic in-script test status and prior effects; separate diagnostic
tests retain responsibility for the message and active descriptor.

Linux UTF-8 controls use the pinned Linux reference and locale. MSYS exclusions
retain their reasons. Known managed departures carry separate native and managed
expectations: a passing departure row does not establish GNU compatibility.
Reference verification collects correctness evidence and no comparative timings.

Keep CPU cancellation, shared allowance/refusal, pending borrowed-write/read/close
cleanup and early-pipe-close controls deterministic. Cancellation requests do not
end storage ownership; assertions must await every pending user and cleanup before
checking refunds. Performance instrumentation belongs to the benchmark lane and
must account for asynchronous continuations separately from live-memory admission.
