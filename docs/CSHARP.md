# C# guidelines

Follow existing code and search for reusable implementations before adding logic.
Prefer concrete names that describe the value or operation.

## Failures and API changes

For expected failures, implement the non-throwing `TryFoo` operation first; implement
`Foo` through it. Preserve explicit failure causes, cancellation and cleanup errors
rather than collapsing them into absence or a generic exit status.

Do not add default values to method or constructor parameters. Keep the full call
explicit; provide an overload or separately named convenience operation when a
shorter call is common and its semantics are clear.

Update known callers and implementations together when changing constructors or
interfaces. Do not add compatibility shims or parallel interfaces speculatively.
This package has external consumers: assess public API compatibility and document
breaking changes in the changelog under the package's versioning policy.

## Values and collections

Prefer non-nullable designs. Use nullable types when absence belongs to the model.
Validate external or deserialized inputs at their boundary; do not scatter defensive
null checks for non-nullable internal values. Fix the contract instead. Do not use
the null-forgiving operator (`!`).

Use enums for closed sets and dedicated types for identifiers, paths, descriptors
and validated values. Reuse existing types before introducing another. Parse once
into a type whose invariants downstream operations can trust; avoid repeated
`IsValid` checks or interchangeable primitive arguments.

Use `IReadOnlyList<T>` and `IReadOnlyDictionary<TKey, TValue>` for read-only contracts.
They do not freeze their backing storage: callers must honor retained-input
immutability, or the receiver must take an owned copy. Use
`System.Collections.Immutable` when efficient copies with changes justify it.

Preserve byte-valued shell data. Decode text explicitly at a boundary requiring
valid UTF-8, retaining the documented buffer lifetime. Prefer `System.Text.Json`
for new serialization code, including tests.

## Ownership and concurrency

Before changing synchronization, identify each mutable object's owner and all
callers, including failure, cancellation and cleanup. Async continuations can
remain one serialized producer; changing threads alone does not establish a race.

Do not make a latency-critical owner loop wait on another thread. Locks,
semaphores, incomplete task waits and spin loops can all block progress when that
thread is delayed. Prefer the existing owner inbox or independent ownership.
Never hold a host-wide lock across awaited descriptor I/O; pipeline producers and
consumers must progress independently. Test progress with controlled delayed work.

Use `ConfigureAwait(false)` when library code awaits host work. Await all users and
uncancellable cleanup before releasing owned resources; cancellation alone does
not end a borrow. Follow the [memory design](MEMORY.md) for admission and refunds.

## Helpers and extensibility

Make a helper with one caller a local function within that caller. Prefer
file-scoped namespaces, small sealed/internal types and readonly value types where
appropriate to their ownership.

Document the required semantics of interface, abstract and virtual members,
including ownership, failure, cancellation and concurrency obligations.

Do not add `InternalsVisibleTo` declarations to project files. Keep intentional
friend declarations in the existing assembly metadata; do not widen production
APIs solely to expose implementation details to tests.
