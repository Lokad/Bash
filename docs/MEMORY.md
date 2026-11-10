# Cooperative memory admission

The host shares one allowance across cooperating libraries and nested commands so
an exceptional command can fail cleanly and later work can proceed. Libraries keep
their own hooks and lightweight adapters, without a host dependency or shared
governor package.

## Estimates and owners

Reserve major backing capacities, retained pools/caches and temporary peaks before
allocating, including overlapping replacement buffers. Release after ownership ends;
the allowance becomes reusable immediately, independently of GC. Count estimated
live ownership, not cumulative allocations.

Use coarse execution, batch, result and cache owners. Roughly 20% unaccounted memory
is intentional latitude for small objects, metadata and short-lived temporaries
that remain minor in aggregate; do not prove a coverage percentage per workload.
The host also leaves headroom for runtime/native allocations and delayed collection.
Approximate sizes and coverage; preserve correct admission, ownership and refunds
for tracked storage. Refine estimates only for measured material gaps, without
per-object reservations or reference-graph tracking.

## Contract

The hooks live in [`Lokad.Bash.Memory`](../src/Lokad.Bash/Memory/IMemoryBudget.cs):

| Contract | Responsibility |
| --- | --- |
| `IMemoryBudget` | Admit estimates and create empty scopes sharing the root allowance. |
| `IMemoryScope` | Own reservations and child scopes; disposal refunds and invalidates its subtree. |
| `IMemoryReservation` | Hold an estimate in `Bytes`; `TryResize` replaces it atomically on success. |

Scopes reserve nothing on creation and have no independent cap. Admission is
thread-safe, immediate and non-waiting. Refusal changes no accounting; failed
resizing preserves the old charge. Zero is valid; negative estimates are errors.

Disposal is idempotent and non-throwing; a scope refunds and invalidates its subtree.
`Bytes` becomes zero after reservation or ancestor disposal. Disposed owners reject
admission/resizing. Owners serialize scope disposal with subtree operations and
reservation resizing with disposal. Disposal refunds accounting; it neither cancels
work nor disposes runtime objects.

Use adapters to the application's governor or the provided `MemoryGovernor`.
Its `LimitBytes`, `ReservedBytes` and `PeakBytes` are estimates, not process memory.
Never copy a remaining balance or create a governor for a nested invocation.

## Calls, results and streaming

Scope creators own disposal; callees borrow supplied budgets and dispose only their
own scopes/reservations. Carry the active budget through every transitive call.
An interpreter owns child scopes it introduces; a host dispatcher owns scopes for
delegated commands. Neither hop requires a new scope automatically. Parent retained
storage stays charged while children and overlapping work run.

Await child calls, canceled reads, borrowed writes, deferred enumeration and cleanup
before disposal. Cancellation does not end ownership. Write completion ends a
borrow only when the API guarantees consumption or copying.

Charge storage to its owner, including caller collections or caches grown by a child.
Borrowed input retains its existing charge; substantial copies need their own.
Returned/lazy results require an owner surviving consumption. Establish it before
allocation: child scopes cannot outlive parents, and there is no automatic
reparenting or transfer to GC.

Release streaming scratch at safe batch/record boundaries; retained state and
reusable capacity stay charged. Do not accumulate every processed record's charge
or refund storage still held by variables/closures. Prefer existing lifetimes,
conservative retention and a focused TODO over a new ownership framework.

## Refusal

Refusal fails the current command with a resource-limit failure. The command/host
boundary reports a diagnostic and nonzero status, awaits cleanup and refunds
relinquished storage. Retained storage stays charged.

Do not recover by retrying, waiting, spilling, switching to streaming, evicting caches
or dropping optional storage. Failed allocation releases unused admission. Refusal
is distinct from cancellation and `OutOfMemoryException`; earlier effects remain,
without rollback or safe replay.

## Bash integration

`Engine.RunAsync` installs the host budget or a supplied evaluation owner.
`BashScript.RunAsync` owns its evaluation scope through descriptor cleanup; host
effects/output retain host owners. Retained evaluator state requires a longer-lived
caller-owned budget.

`CommandAllocationBudget` bounds cumulative expansion work, without refunds; it
does not replace memory admission. A new host sharing parent expansion limits
must separately receive a memory budget under the same root.

`MemoryCapacity` amortizes admission with charged prepaid capacity. Parsing has a
source-size bound, not complete accounting; manual stepping does not install a host
budget. Keep coverage limits explicit. Test shared allowances, refusal, immediate
refunds, retained results and cleanup with pending users.
