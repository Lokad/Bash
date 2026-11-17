# Runtime contracts

`Syntax.Parser` creates immutable syntax. `Evaluation.Evaluation` owns language
state and emits typed operations; `Engine` executes those operations through
`IBashHost`. Nested evaluations and pipelines share this same execution path.
The host owns all external effects and must authorize the capabilities it exposes.

Conditional parsing selects its closing delimiter in the operand's lexical role.
Token-start comments stop only at LF; quotes, expansions and delimiter spellings
inside them stay inert. LF separates complete predicates and precedes terms, but
does not separate a unary/binary operator from its required operand or a lone
string operand from its following operator/terminator. Physical byte offsets
remain attached to source. Nested modern substitutions share the caller's scan
context and transfer their lookahead parse into word construction instead of
rebuilding the child tree at every conditional nesting level. Comment/continuation
edits are recorded during that traversal and admitted to the syntax owner before
normalization storage grows. Display/export spelling omits those lexical edits;
caller-constructed ConditionalCommand.Expression retains its supplied source.

Leading tilde selection borrows the complete unquoted prefix across adjacent word
parts before reading its variable. Quotes, escapes or substitutions before the
first unquoted slash prevent that selection, including empty quoted parts. Parts
after the delimiter expand once through the ordinary word path. Suffix views share
their source-owned arithmetic metadata rather than preparing it again at runtime;
the existing source/command owners retain syntax and values through consumption.

Here-document collection removes unquoted backslash/LF continuations before
delimiter comparison, including for commands whose execution will be skipped.
Tab-stripping applies at the start of each logical line, preserving tabs inside
continued lines. Quoted delimiters retain physical body lines. This normalization
does not expand values or validate reached substitutions; their existing execution
and cleanup path remains late. Token and diagnostic offsets still use physical source.

Explicit and saved directory destinations use one host lookup and publication
path. Each reached attempt asks for directory-kind information, even for a saved
path or the current cwd. A failed lookup preserves cwd and directory variables;
a failed swap still replaces the second stack entry, while a failed pop retains
it. Pending lookup work and descriptor cleanup keep their command owner alive.
This validation does not make a later filesystem change atomic with navigation.

Successful lookup publishes cwd and the stack head before variable bookkeeping.
The shared assignment path retains the selected binding kind, owner and attributes;
OLDPWD receives PWD's prior assignment state before PWD receives the new path.
Push/pop finish only after bookkeeping succeeds. Readonly failures preserve partial
effects; an enabled errexit option invokes the existing EXIT path before builtin
and descriptor restoration. Ordinary tested-status suppression does not defer
this internal readonly-assignment exit.

Nameref declaration validation precedes replacement of values and attributes.
Circular local references reuse scope ownership to select a global fallback;
selected reads retain that store through value consumption. Declaration metadata
does not follow aliases. Builtin outputs share selection and publication, with
explicit getopts failure, printf count and descriptor policies. An allocation
that does not publish its variable remains temporary and uses ordinary cleanup.

Function-call prefixes retain temporary-binding provenance through nested calls.
A first local declaration inherits a visible temporary value; a prefix on the
declaration command itself retains its distinct lifetime. Unsetting a temporary
prefix exposes its saved caller binding, and later writes use that caller owner.
Unsetting an ordinary local retains an unassigned declaration in its own namespace.
Readonly/export promotions and child forks use the same selection and restoration
paths. Temporary namerefs supply raw target text to a fresh ordinary local;
temporary arrays converted inside a function retain their structured values when
inherited by a helper's local declaration.

Legacy command-substitution metadata keeps executable bytes separately from the
original delimited spelling. Function display/export emits that spelling without
parsing the child body, preserving validation timing across replay and import.
The spelling borrows the existing syntax/source owner; execution and capture keep
their shared path. Legacy child syntax is still parsed when each execution reaches it.

Printf retains byte numeric operands through prefix validation, range classification
and diagnostics. Integer accumulation stays bounded after overflow while validity
scanning continues. Floating-point conversion reuses the existing bounded-significand
parser, retaining consumed-prefix and range information for printf's diagnostic
policy. Its coarse numeric workspace is admitted before conversion and reused only
after the preceding conversion has finished. Conversion diagnostics retain their
bytes through borrowed writes; cancellation still drains ordinary descriptor cleanup.

Printf field widths and precision retain integer representation checks while actual
output and numeric workspace use shared admission. Exact finite digits stay bounded
by the extended numeric profile; trailing zeros are emitted through the ordinary
output builder with cancellation checks. General formatting reuses its rounded
coefficient. Refusal retains earlier count assignments and skips later conversions;
returned output remains admitted through its consumer's ownership scope.

Printf time formats borrow their original bytes until conversion. An admitted,
reversible byte/character projection shares the date renderer's ASCII directive
grammar, and invariant directive results map back beside unchanged literal bytes.
GNU's bounded strftime result is selected before ordinary outer width/precision.
Projection and rendering workspace share the current conversion owner; copied
output keeps the ordinary result and descriptor-write lifetime.

Command dispatch resumes asynchronously when CLR stack space runs low, so finite
function calls do not depend on the embedding thread's native stack size. Reached
source/eval parsers start on a fresh continuation to keep syntax guards independent
of execution depth. These continuations retain the same evaluator, bindings,
admission and descriptor owners through completion and cleanup.

Sourced function definitions share the input's admitted owner across namespaces
and attribute copies. An invocation retains its selected definition through
preparation and descriptor cleanup, including self-unset/replacement. Nested
definitions inherit the executing function's source, independently of its caller.
Retired bindings and settled child namespaces release their ownership; copied
eval/trap source does not keep unrelated source-file buffers admitted.

Each immutable function binding shares its serialized export across attribute
copies and child namespaces. First serialization charges the creator's installed
evaluation owner. Unset/redefinition releases retired storage after the last
namespace, invocation, query or host-command borrow finishes, including descriptor
cleanup. Concurrent publication never waits on another evaluator; unused candidates
are refunded, while admission refusal remains a failure. Hosted consumers that
retain export bytes after command completion own and admit their copies.

Line-continuation normalization reuses the structural scan's nested syntax while
retaining physical source positions. Reusing lookahead through a source slice
does not cross its end or reset the structural nesting guards.

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

Named bracket atoms share their decoder with suffix atom preparation. Character
class names and ASCII membership share internal vocabulary with regex translation,
while their grammars retain distinct supported profiles. Per-match value state
reuses terminal lookahead across overlapping literal fallbacks; it adds no global
cache or subject copy. Repeated valid bracket scans on wildcard retries remain
separate from this malformed-pattern work.

Field-separator lookup retains byte-slice identity and resolves current nameref
targets. First-character joins stay independent of membership preparation. Reached
splitting prepares only its scalar/byte profile through the active command's
allowance, then publishes an immutable snapshot in that variable scope. Clones
share completed indexes; a child never grows its parent's snapshot. Read splitting
uses the same membership preparation, with its own escape/record policy.

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

Loop flow is settled at the loop that owns its remaining level. In while/until
conditions, the condition gate precedes body suppression: a closed gate retains
the previous body result, while an admitted but skipped body retains the tested
condition status. Outward flow carries that status and its tested origin. Local
body continue still advances for-in iteration or performs arithmetic-for updates.

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

Allocated descriptor variables require adjacent unquoted `{name}` syntax and use
the ordinary open, duplicate and here-input paths. In-process allocations persist
up to an earlier temporary restoration boundary; external-command allocations and
their selector effects stay in the command scope. `{name}>&-` and `{name}<&-` read
the current binding without assignment, including readonly names and selected
nameref elements. Their closes are temporary unless `exec` preserves them.

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
Registration storage is admitted before pipe creation, reuses its high-water
capacity and stays charged through the final awaited close. The execution cleanup
owner retires this storage after all registered producers and readers settle.
Closing batches prepare capacity before acquiring handles. Redirection transfer,
preservation and final cleanup share the serialized execution batch, preserving
scope order, reverse-open transfer order and once-only closure without late
admission during unwind. Existing borrow indexes protect parent subshell handles.
Runtime child-descriptor collections borrow their parent command's admitted
workspace through all child consumers and cleanup. Inline subshells share the
completed collection's index; its nearest entry covers every older borrowed scope.
Command substitution and host invocation build one union while selecting command
ownership before adding nested-only borrows. Public context preparation remains
caller-owned and carries no implicit interpreter budget.

Logical descriptor numbers are evaluator state. Host handles are capabilities and
can have multiple aliases. Borrowed handles stay owned by their caller. Owned
handles are closed after their last alias is released; close is uncancellable and
retires the handle even when cleanup fails. Preserve earlier failures and finish
all users before releasing memory or returning a command's completion.

Each redirection lifetime retains one ordered handle owner across setup,
execution and restoration, keeping its setup frame installed. Persistent publication prepares every receiving map
and its handle ownership before changing the binding. Forked state borrows the
index until a writing owner detaches. Transfers settle new ownership in open order
and preserve existing ownership, including handles borrowed by inline subshells.
Cleanup transfers into prepared ownership
and retires the index after its final close attempts; logical mapping retention
remains independent of host-handle cleanup.

Captured additional bindings share immutable lookup and ordered-slot trees;
changed mappings copy only affected paths. Standard streams stay inline, explicit
closed mappings remain present, and free-slot reuse/encounter order retain their
previous capture boundaries. Sparse compaction is amortized across deletions.
Preparing all receiving estimates changes no binding or ownership; only successful
publication acquires a writing table's retained storage. Snapshot sharing does not
transfer handle-closing responsibility or add another execution/cleanup path.
Reference membership uses handle counts and the active writer's pending changes;
snapshots retain frozen counts. Remaining aliases survive when one binding closes or moves.
Counts merge at capture through preadmitted storage. A live-key list makes the
merge proportional to pending changes after an earlier wide batch; cleanup performs
only lookups and retains the existing once-only handle closure path.

Descriptor redirections prepare source move syntax on their immutable node and
resolve its operand against current bindings. Valid filename fallback opens the
already expanded target; it does not repeat its effects. Closing or moving a
logical descriptor uses the same alias/restoration and cleanup path. Invalid
descriptor words report shell failures rather than escaping implementation exceptions.

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

`BashScript.RunAsync`, `source`, `eval` and reached EXIT actions parse and execute complete command units
through the same evaluator. A newline completes a unit only after its compound
syntax, continuations, quoting and here-documents are complete. Later syntax errors
retain earlier units' effects; an invalid unit executes none of its commands.
Explicit `Parser.Parse`/`Parser.TryParse` and prepared `Evaluation` execution retain
whole-script validation. Recoverable arithmetic/assignment expansion failures end
their current unit; unrecoverable failures and explicit exit stop the script.
Runtime source copies and parsed units retain coarse admitted ownership through
surviving definitions and active users. Completed unrelated units release their
metadata; deferred substitutions use the same parser with a reached syntax owner.
Memory refusal while preparing an EXIT action reports through its active stderr
mapping and returns status 1 through ordinary cleanup.
EXIT actions retain their own flow/status policy: normal completion preserves the
triggering status, while return/exit and errexit can replace it. They do not inherit
tested-status suppression from the caller. Explicit exit invokes the action before
active bindings, loop targets and redirections unwind; arity failure remains distinct.
