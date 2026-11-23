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

Tilde selection reads the complete unquoted prefix across adjacent word parts
before resolving its value. Reached assignment colons open additional prefixes
in source order. The word walker resumes at the original part and byte offset.
That offset addresses the existing source-owned arithmetic metadata. The
source and command owners retain syntax and expanded bytes through consumption.

Here-document delimiter words keep literal structured spelling and determine
body expansion from outer quoting. Collection and function serialization share
the resulting closing bytes. Here-document collection removes unquoted
backslash/LF continuations before delimiter comparison, including for commands
whose execution will be skipped.
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
When `..` would remove a directory component supplied by an operand, the
evaluator checks that directory before continuing lexical normalization.
Consecutive parent components reuse the checked prefix. Each settled prefix
retains only its normalized spelling as the basis of the next segment.
The final normalized destination still receives its own directory lookup.

CDPATH tries candidates in component order using the same host lookup. A
failed candidate leaves navigation and the stack alone. Each candidate's
temporary path budget stays live until its lookup settles and can be reused
for the next component. The selected candidate enters the shared publication
path once. If the search finds no directory, the ordinary destination
supplies the final lookup and any diagnostic.

Successful lookup publishes cwd and the stack head before variable bookkeeping.
The shared assignment path retains the selected binding kind, owner and attributes;
OLDPWD receives PWD's prior assignment state before PWD receives the new path.
Push/pop finish only after bookkeeping succeeds. Readonly failures preserve partial
effects; an enabled errexit option invokes the existing EXIT path before builtin
and descriptor restoration. Ordinary tested-status suppression does not defer
this internal readonly-assignment exit.

`cd` expands its operands once in the caller scope, then checks arity before
decoding the selected path. HOME and OLDPWD are read from the active command
bindings when selected. The `cd -` announcement uses the original path bytes
after a successful host lookup and before directory-variable bookkeeping. The
existing write and cleanup path retains the output through completion. A
reported write failure keeps the completed navigation and fails the command.

Stack-only edits preserve the current directory and skip directory-variable
assignment. Deferred entries own their literal bytes and child stacks borrow
that backing while their parent is live. Selecting one for navigation then
uses ordinary path decoding, host lookup, and directory bookkeeping. Successful
push and pop operations report a failed listing write through active stderr
while keeping the completed stack change and its status.

Nameref declaration validation precedes replacement of values and attributes.
Circular local references reuse scope ownership to select a global fallback;
selected reads retain that store through value consumption. Declaration metadata
does not follow aliases. Builtin outputs share selection and publication, with
explicit getopts failure, printf count and descriptor policies. An allocation
that does not publish its variable remains temporary and uses ordinary cleanup.

Integer assignment evaluates the already expanded operand through the shared
arithmetic evaluator before publishing its decimal value. The selected binding
supplies the attribute and any append value. Arithmetic writes already carry
decimal results through publication. Inherited arrays have their own admitted
storage and are restored with the same function-local lifetime as other bindings.

`getopts` keeps its argument index and bundle offset in the variable owner.
Explicit writes to `OPTIND` update the scan even when the visible value is
unchanged. Function-local bindings restore the saved cursor, and child evaluations
advance their own cursor. The scanner retains only indexes and an offset.

`mapfile` and `readarray` validate the target before reading. They collect
bounded batches from the default or explicitly selected input descriptor.
Skipping releases each discarded batch before the next read. A positive count
caps the retained records and the final batch. Without a callback, publication
follows that count or successful EOF, once replacement storage is admitted.
The staged record list admits overlapping backing during growth and returns
the previous array's allowance after replacement. Ordinary array entries
receive compact values whose storage can be reclaimed as entries are replaced
or removed. An explicit origin updates existing sparse positions in place.
Integer-array input evaluates records in order while the original binding is
kept for refusal and cancellation. A shell arithmetic error retains preceding
array writes and their arithmetic effects.
A failed or canceled read leaves the prior array intact.

With `-C`, a batch ends at the next callback record. Earlier records are
visible during the callback and the current record is stored when it returns.
The parsed callback uses the active bindings and descriptor frame. The reader
settles the callback before dispatching more input. Cancellation or failure to
read or admit input restores the saved target. Explicit flow out of the
callback carries the completed prefix.

`read -a` validates the array destination after consuming its logical record.
It shares the field scanner with scalar `read` and publishes all split fields
in one admitted replacement. Array destinations follow whole-name namerefs.
Temporary command bindings remain in effect during the array assignment.

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
The spelling borrows the existing syntax/source owner. Parsing begins when
execution first reaches the substitution. Successful parses from stable
source can be reused by later executions under the same owner. Ordinary command
substitutions use the shared execution and capture path with fresh shell state.
The single input-file form expands its target in the caller, reads bounded bytes
through the host and closes the temporary redirection before value expansion
resumes. The capture owner stays live until the host read and cleanup settle.
An open failure under errexit ends the caller's expansion. Ambiguous targets
return a substitution failure status through the ordinary expansion path.
The delimiter scanner records syntax failures that would arise from rereading a
parenthesized substitution's rendered spelling. Reached expansion reports the
failure before setting up capture descriptors. Prepared top-level lists retain
complete command-unit boundaries. Both execution paths use the same recovery
policy and publish the failure status before resuming the next unit.

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
Explicit seconds use a repeating Gregorian cycle to derive local calendar fields
and retain the original Unix seconds alongside them.
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

Command queries settle host lookup and borrowed writes before releasing their
arguments, buffered output and borrowed function definitions. `type -f` reuses
the existing function-bypass lookup policy. `type -p` selects the ordinary
resolution and emits a path only for an external result. `type -P` sends the
selected names to the same host lookup with the reached directory and environment.

Function listings capture the sorted names before the first write. Each
row looks up its current function and attributes when reached, so a host
publication during a write can change a later row. The listing borrows
caller-owned names and copies names tied to evaluator-owned source. It
holds the snapshot through output and lets retired source owners settle
independently.

Line-continuation normalization reuses the structural scan's nested syntax while
retaining physical source positions. Reusing lookahead through a source slice
does not cross its end or reset the structural nesting guards.

The first top-level closer in a `$((` candidate selects arithmetic when it starts
a pair of logical closing parentheses. A single closer with following input
hands delimiter selection to the command parser. Command lookahead records its
required expansion and command depth, including reused descendants. The
tokenizer can transfer a cached tree to a different depth within both budgets.
When the body grammar diagnoses a malformed parenthesized substitution, the
scanner carries that diagnostic to the enclosing command and retains the source
offset. Missing closing delimiters keep their own diagnostics. A diagnosed
substitution in expanded here-document data is reported during redirection
setup, before the captured body runs.
Reached backticks reuse successful prepared syntax through the source owner.
If preparation finds malformed syntax, its speculative storage is refunded
before the capture reads complete command units. That path can retain earlier
output and effects while discarding the failed unit. Structural limit failures
return their diagnostic directly.

Arithmetic commands, arithmetic-for clauses and ordinary word arithmetic
expansions retain source-owned lexical decisions for stable source. Numeric values,
operator classifications and raw references are immutable; execution retains the
existing grammar driver, structural guards and numeric work pump. Bindings, selector
expansions and writes use the current scope. Dynamic shell splicing still prepares
fresh operands, and syntax failures remain at their reached execution point.

Literal-only single-part words retain their expanded fields and quote flags on
the immutable word, borrowing its byte storage. A prepared literal command field
stays in its original word when it needs no pathname expansion. Expansion
continues to check for pending failures and cancellation. Tilde, brace, parameter
and other dynamic syntax stay late-bound. Pathname expansion scans components in
the existing pattern and quote mask. It prepares the literal prefix once and
keeps separate spellings for output and lookup. Lookup preserves dot and dot-dot
components for host traversal.

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
and filesystem execution with `[[`. Variable predicates share the binding and
array selector resolver. A lexical conditional selector retains its own word
and expands once when reached, before indexed arithmetic or associative lookup.
In `test`, both sides of `-a` and `-o` are evaluated. Expressions with variable
predicates evaluate file and variable predicates in argument order.

Declaration and key/value transformations share the variable display flags and
array order. The transform uses the selected read owner and selector result.
Quoted rendering holds temporary capacity against the shared allowance until
the word builder consumes its bytes. Separate key/value fields stream into the
word builder in array order and use the existing splitting and joining rules.

Shell-word pattern matching shares one traversal core for complete matches,
prefixes, suffixes and replacement search. Suffix traversal reads characters
backward and retains bracket expressions as prepared atoms; it does not reverse
or copy the subject. Replacement searches track the leftmost start and longest
match in one shared search. Literal searches use byte spans. Prepared offsets belong
to the current command and are admitted once per reached character profile.

Named bracket atoms share their decoder with suffix atom preparation. Character
class names and ASCII membership share internal vocabulary with regex translation,
while their grammars retain distinct supported profiles. Per-match value state
reuses terminal lookahead across overlapping literal fallbacks. A wildcard retry
that revisits a wide, valid bracket prepares its membership for that match.
Subsequent candidates use the merged intervals and fixed character classes.
Prepared membership follows the original atom and character profile in every
traversal. Subjects stay borrowed throughout matching.

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
The `local -` operand saves errexit on the active function invocation, including
when it is reached through eval or source. Normal return and expansion failure
unwind the snapshot. A terminating exit or errexit lets the EXIT action see the
active option state. A child evaluation forks the function frames and their
snapshots alongside the ordinary shell state.

Loop flow is settled at the loop that owns its remaining level. In while/until
conditions, the condition gate precedes body suppression: a closed gate retains
the previous body result, while an admitted but skipped body retains the tested
condition status. Outward flow carries that status and its tested origin. Local
body continue still advances for-in iteration or performs arithmetic-for updates.
For-in assignments preserve array kind and update element zero. A nameref
selects a new target for each item. An invalid target stops the loop with a
diagnostic and failure status exempt from errexit at the loop boundary. A
readonly loop binding exits its shell when errexit is enabled, including in a
tested position.

Foreground commands admit space for `PIPESTATUS` before execution. Publication
uses the command's admitted workspace through descriptor cleanup. It creates
missing metadata and updates indexed bindings, including reached nameref
targets. Declaration-only indexed status entries remain hidden until an explicit
assignment exposes the array. Simple commands and pipelines publish before negation.
Arithmetic and conditional commands publish the final status when they return
normally, while most other compound forms keep the last command's metadata.
Ordinary builtins with a temporary `PIPESTATUS` assignment keep the caller's
metadata. Functions, eval, source, unset, read and mapfile can publish after
restoring the caller's scope.

Positional parameters use owned argument frames behind a stable live read-only
view. Shifting advances a head index and releases consumed value references;
surviving entries are not moved. Functions and argument-bearing source calls
retain the caller frame and restore it without copying its entries. A new frame
copies callee entries once while sharing immutable value bytes. Functions always
restore their caller. A source call with arguments restores its caller inside
a function. Outside functions, it can keep an explicit `set` replacement.
Source entry clears the replacement marker even when the active frame is reused.
Function restores and argument-bearing source returns also clear it. Shifts
preserve the marker and child evaluations isolate it. Saved frames remain
admitted through their surrounding command scope.
An unset positional scalar such as `"$1"` emits one empty field on its own in
double quotes. An aggregate that emits zero fields suppresses empty expansions
inside the same double quotes. Supplied empty aggregate elements and separate
quoted literals keep their fields. The tokenizer retains quote boundaries across
substitutions and escapes. Function display and export preserve those boundaries
when the definition is evaluated again.

Prefix assignment preparation borrows caller state for the surrounding command.
It admits each detached binding once, retaining independent array containers
and attributes. Read-only presence, attribute and nameref queries inspect
the visible binding in its existing owner. The existing expansion write
target forwards actual RHS effects, including same-value writes, with nameref and
array-kind rules intact. In-process execution consumes only the prepared prefix
slots and preserves the original slots in the saved store. An installation
refusal leaves the caller's bindings at their state after operand expansion.
Full child snapshots still detach all visible state.

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
Trailing subshell redirections expand within the child bindings. The child
keeps their descriptor frame through its EXIT handler and closes its own
handles before returning to the parent.

An explicit attribute promotion of a temporary prefix can survive local
restoration, including through nested functions. Direct unset in the owning local
scope cancels promotion; redeclaring a local after a helper unset starts a new local
lifetime. Isolated child contexts retain their own promotion/restoration state.

Host calls can overlap while pipeline stages execute. Allow producers and consumers
to progress independently: never await descriptor I/O while holding a host-wide
filesystem lock. Pipes must preserve byte order and apply backpressure. A consumer
closing early is distinct from a write failure.
An exceptional stage failure requests cancellation from the other stages while
their cleanup is still active. The first reported failure survives cancellation
and cleanup. The dispatcher waits for all stages and their endpoint closes before
returning. Ordinary shell exit statuses still settle independently.

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
Preservation observes replaced handles in encounter order directly from the
setup collection. It checks the preserved table and the remaining owners
before closing each candidate.

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
already expanded target without repeating its effects. A move to the same logical
number is a no-op. A non-allocated move to a closed destination can consume the
source across an in-process command boundary. Earlier redirections that saved the
source restore it at their own boundary, including when both bindings use the
same host handle. An open destination keeps the source close temporary unless
`exec` preserves it. Child moves retain their own bindings. Source handles retire
through the ordinary alias and cleanup path after their last reference.
Invalid descriptor words report shell failures through the active descriptor map.
Ambiguity diagnostics use the reached stderr mapping before the frame is restored.
Their expansion preserves direct substitution spelling and runs even when stderr
is closed. Diagnostic bytes remain admitted through completion of the write.

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

The evaluator retains the last byte argument from ordinary argument expansion
for `$_`. A completed simple command publishes it after invocation and
redirection cleanup, before an ordinary failure can reach errexit. An explicit
exit action sees the value from preceding commands. The binding keeps only
compact backing and uses the existing command allowance to admit retained
storage after temporary consumers settle.

`BashScript.RunAsync`, `source`, `eval` and reached EXIT actions parse and execute complete command units
through the same evaluator. A newline completes a unit only after its compound
syntax, continuations, quoting and here-documents are complete. Later syntax errors
retain earlier units' effects. A direct double-quoted command substitution with
a malformed arithmetic `for` header can end the input at that word. The evaluator
reports the diagnostic and sets the parser status to 2 before running any prefix
that forms a complete command list there. The prefix supplies the unit's final status.
`source` and `eval` select operands from completed byte fields after command
resolution. They validate leading options before performing a file lookup or
allocating copied script source. `source` decodes its selected filename and
copies its positional argument tail directly into the callee frame.

Positional substring expansion resolves the bounds before reading the selected
arguments. It indexes the active window from the selected position and includes
the captured `$0` when a nonempty slice starts at zero.

Associative slices seek by ordinal in unsigned-byte key order. The same tree
provides key lookup, ordered traversal and range selection. Its subtree counts
stay current through inserts and removals. Slice enumeration starts after the
reached bounds have expanded and sees changes to the selected array.

`caller` merges active sourced invocations with the function chain.
Each source command owns its immutable frame across execution and child
cleanup. Child evaluations borrow the frame chain and preserve the order
relative to their reconstructed function scopes. A retained function
definition keeps its source bytes independently of any active invocation.
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
active bindings, loop targets and redirections unwind. A required-parameter exit
under errexit also uses its active lexical frame. Arity and recoverable expansion
failures retain their existing unwind paths.

An unhandled status retains active positional and control-flow frames for EXIT.
Simple failures settle the nearest reached simple invocation with restorable
redirections first. Compound failures run through the reached descriptor mapping.
Ordinary shell command units restore local and temporary bindings before the action.
Direct child execution retains them. Eval and source restore bindings at their
command-unit boundary. Pending timers report once, before the action. The
enclosing descriptor and command owners still settle before completion returns.
