# Runtime contracts

`Syntax.Parser` creates immutable syntax. `Evaluation.Evaluation` owns language
state and emits typed operations; `Engine` executes those operations through
`IBashHost`. Nested evaluations and pipelines share this same execution path.
The host owns all external effects and must authorize the capabilities it exposes.

Host calls can overlap while pipeline stages execute. Allow producers and consumers
to progress independently: never await descriptor I/O while holding a host-wide
filesystem lock. Pipes must preserve byte order and apply backpressure. A consumer
closing early is distinct from a write failure.

Logical descriptor numbers are evaluator state. Host handles are capabilities and
can have multiple aliases. Borrowed handles stay owned by their caller. Owned
handles are closed after their last alias is released; close is uncancellable and
retires the handle even when cleanup fails. Preserve earlier failures and finish
all users before releasing memory or returning a command's completion.

`CommandInvocation` carries byte arguments, environment, working directory, stdio,
exported function definitions, ownership and an optional typed interpreter payload.
Same-host Bash startup imports captured bindings. New-host startup uses its own
environment and descriptors. Child commands preserve captured descriptor bindings,
exported functions and the working directory without taking ownership of borrowed
handles. The host owns and admits any copies made by its argument consumers.

Read buffers are borrowed until disposal. Mark the consumed prefix before disposal;
unconsumed bytes remain available through aliases. Retain or copy bytes before
their owner ends. Host snapshot tokens select immutable content versions without
opening streams; path mutation cannot change a captured version.

Memory admission and scope ownership follow the [memory design](MEMORY.md).
Completed commands relinquish their scratch storage; execution has no cumulative
word, character, brace, argument-copy or pattern-step allowance.

Caller cancellation controls evaluation lifetime. A separate token can wait for
the next operation without rebinding that lifetime. Cancellation drains descriptor
cleanup before escaping. Applications supply finite deadlines and must ensure their
callbacks honor cancellation. Timing and optional execution monitoring come from
the host; no ambient process environment or real filesystem is consulted by evaluation.
`LoggingBashHost` explicitly opts into console tracing of host operations.

`BashScript.RunAsync` returns exit status and final working directory, reports
syntax failure on stderr and releases its evaluator memory scope. `Engine.RunAsync`
can instead use a caller-owned evaluation memory scope when state must be retained.
Keep that scope alive as long as retained evaluator data is used.
