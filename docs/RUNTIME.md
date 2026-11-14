# Runtime contracts

`Syntax.Parser` creates immutable syntax. `Evaluation.Evaluation` owns language
state and emits typed operations; `Engine` executes those operations through
`IBashHost`. Nested evaluations and pipelines share this same execution path.
The host owns all external effects and must authorize the capabilities it exposes.

Arithmetic commands, arithmetic-for clauses and ordinary word arithmetic
expansions retain source-owned lexical decisions for stable source. Numeric values,
operator classifications and raw references are immutable; execution retains the
existing grammar driver, structural guards and numeric work pump. Bindings, selector
expansions and writes use the current scope. Dynamic shell splicing still prepares
fresh operands, and syntax failures remain at their reached execution point.

Literal-only single-part words retain their expanded fields and quote flags on
the immutable word, borrowing its byte storage. Expansion still observes pending
failure and cancellation; tilde, brace, parameter and other dynamic syntax stay
late-bound. Pathname lookup retains the ordinary active quote mask and host path.

Command lists prepare immutable navigation once per reached syntax root. Each
execution keeps its cursor, status and scope locally and dispatches leaves through
the same evaluator. Short-circuit jumps skip complete right subtrees, while
errexit suppression combines the source position with the current invocation
context. Preparation and execution are iterative and cancellable; cache publication
does not wait for another execution. Record operand changes invalidate navigation.

Conditional commands retain parsed operators and operand words on the script.
Execution expands reached operands against the current scope, preserving quotation
and bytes; short-circuited operands cause no expansion or host work. Reached file
predicates use the existing file-operation path in source order. The distinct
`test`/`[` grammar consumes completed byte arguments and shares predicate metadata
and filesystem execution with `[[`.

Shell-word pattern matching shares one traversal core for complete matches,
prefixes, suffixes and replacement search. Suffix traversal reads characters
backward and retains bracket expressions as prepared atoms; it does not reverse
or copy the subject. Replacement searches track the leftmost start and longest
match in one shared search. Literal searches use byte spans. Prepared offsets belong
to the current command and are admitted once per reached character profile.

Regex matching publishes independently owned capture bytes to global
`BASH_REMATCH`, after admitting the replacement array and byte storage. Nonmatches
publish an empty array; errors leave the previous binding intact. Function-local
shadows do not change this global publication owner. The qualified matcher and its
individual-call cancellation limits are specified in [compatibility](COMPATIBILITY.md).

Errexit is shell state. Execution context records whether a status is tested and
carries that context through functions, groups, source/eval and child evaluations.
Compound commands preserve the tested origin of their final status; simple
invocations expose a fresh status. Early exit follows the ordinary EXIT-trap and
descriptor cleanup path. Command substitutions clear errexit on entry, while
subshells and pipeline stages inherit it.

Completed foreground commands publish `PIPESTATUS` after their operands expand.
Pipelines publish the settled stage statuses before applying negation; compound
syntax preserves the last executed command's metadata. Publication follows binding
ownership in the active shell and uses the command's admitted workspace.

Positional parameters use owned argument frames behind a stable live read-only
view. Shifting advances a head index and releases consumed value references;
surviving entries are not moved. Functions and argument-bearing source calls
retain the caller frame and restore it without copying its entries. A new frame
copies callee entries once while sharing immutable value bytes. Functions always
restore their caller; source restores unless an explicit positional replacement
survives. Saved frames remain admitted through their surrounding command scope.

Prefix assignment preparation borrows caller state for the surrounding command.
It admits and copies each reached binding once, retaining independent array
containers and attributes. Unrelated names and containers remain with the caller;
metadata enumeration does not copy their values. The existing expansion write
target forwards actual RHS effects, including same-value writes, with nameref and
array-kind rules intact. In-process execution installs only prepared prefixes on
the active owner; full child snapshots still detach all visible state.

External-command redirection expansion uses the same command-local detachment.
Redirections share their temporary writes in order, while caller bindings remain
unchanged. Literal targets need no temporary variable state. In-process commands
and `exec` retain their caller-visible redirection effects.

An explicit attribute promotion of a temporary prefix can survive local
restoration, including through nested functions. Direct unset in the owning local
scope cancels promotion; redeclaring a local after a helper unset starts a new local
lifetime. Isolated child contexts retain their own promotion/restoration state.

Host calls can overlap while pipeline stages execute. Allow producers and consumers
to progress independently: never await descriptor I/O while holding a host-wide
filesystem lock. Pipes must preserve byte order and apply backpressure. A consumer
closing early is distinct from a write failure.

Here-input producers start independently before their consumers. Their payload
copies have separate admitted ownership, surviving command workspace reuse when
an `exec` descriptor persists. The reader's last owner retires the input, stops an
unused writer and awaits borrowed writes and both endpoint closes. Producer failure
also retires the reader so a consumer cannot remain stranded waiting for EOF.

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

`BashScript.RunAsync`, `source` and `eval` parse and execute complete command units
through the same evaluator. A newline completes a unit only after its compound
syntax, continuations, quoting and here-documents are complete. Later syntax errors
retain earlier units' effects; an invalid unit executes none of its commands.
Explicit `Parser.Parse`/`Parser.TryParse` and prepared `Evaluation` execution retain
whole-script validation. Recoverable arithmetic/assignment expansion failures end
their current unit; unrecoverable failures and explicit exit stop the script.
