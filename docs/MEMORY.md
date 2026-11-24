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

The [`Lokad.Bash.Memory` interfaces][memory-interfaces] define these responsibilities.

| Contract | Responsibility |
| --- | --- |
| `IMemoryBudget` | Admit estimates and create empty scopes sharing the root allowance. |
| `IMemoryScope` | Own reservations and child scopes; disposal refunds and invalidates its subtree. |
| `IMemoryReservation` | Hold an estimate in `Bytes`; `TryResize` replaces it atomically on success. |

[memory-interfaces]: https://github.com/Lokad/Bash/blob/master/src/Lokad.Bash/Memory/IMemoryBudget.cs

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

`MemoryCapacity.Bytes` records the helper's logical usage and clears on its own
disposal or detachment. A live reservation reports the prepaid charge, which its
enclosing scope can refund. Capacity adjustments require a live enclosing scope,
including zero growth and same-size resize. An unused helper keeps reservation
creation deferred until it needs capacity.

## Calls, results and streaming

Scope creators own disposal; callees borrow supplied budgets and dispose only their
own scopes/reservations. Carry the active budget through every transitive call.
An interpreter owns child scopes it introduces; a host dispatcher owns scopes for
delegated commands. Neither hop requires a new scope automatically. Parent retained
storage stays charged while children and overlapping work run.

Await child calls, canceled reads, borrowed writes, deferred enumeration and cleanup
before disposal. Cancellation does not end ownership. Write completion ends a
borrow only when the API guarantees consumption or copying.

An array-reader callback holds its selected input batch through nested
evaluation. Its generated command has a source owner that survives any
function definition retained from that command. The saved array is charged
through settlement while the visible array receives individually admitted
records. Refusal by the array reader or cancellation restores the saved
binding.

Charge storage to its owner, including caller collections or caches grown by a child.
Borrowed input retains its existing charge; substantial copies need their own.
Returned/lazy results require an owner surviving consumption. Establish it before
allocation: child scopes cannot outlive parents, and there is no automatic
reparenting or transfer to GC.

Supply a caller-owned scope to standalone `printf` and timing formatters and to
whole-input readers that materialize results under a supplied budget. Keep the
scope alive through the last consumer, including any pending host write, then
dispose it to release the result's charge. A root governor has no result lifetime
to dispose, and the returned byte memory carries no reservation handle.

Release streaming scratch at safe batch/record boundaries; retained state and
reusable capacity stay charged. Do not accumulate every processed record's charge
or refund storage still held by variables/closures. Prefer existing lifetimes,
conservative retention and a focused TODO over a new ownership framework.

## Refusal

Refusal fails the current command with a resource-limit failure. The command/host
boundary reports a diagnostic and nonzero status, awaits cleanup and refunds
relinquished storage. Retained storage stays charged.

Expansion diagnostics and direct host error diagnostics admit buffers larger
than 256 bytes through stderr write completion. Short messages fit in caller
headroom. Expansion and builtin invocation errors can supply borrowed name
and message fragments. The formatter copies them directly into the admitted
record and returns the write result to callers that select their status from it.
Shell `printf` admits the list of pending diagnostic fragments and reuses its
capacity between conversions. An emitted record stays charged through the
stderr write. Standalone formatting that returns diagnostics owns their
materialized bytes under the caller's scope. Output-only formatting and
shell-closed stderr skip diagnostic storage.
`printf -v` admits the retained prefix of its formatted output. It counts
the first NUL and later bytes without storing them. Quoted fields with zero
width and no output precision use the final buffer and roll back an
incomplete field at the output limit. Conversions that stage their bytes
carry their logical lengths into field padding and precision.
Their output scratch retains a prefix while those bytes can reach the
assignment. `%b` and `%q` cap that scratch prefix at output field precision
for both stdout and assignment. Decoding and quoting still count the full
conversion. The buffer is reused between conversions and discarded before
reached count assignments and diagnostic writes. Assignment compacts a
truncated value before the formatting buffer is released.
Long literal format and `%b` runs append bounded spans to the same output
buffers. Cancellation checks separate spans, and escape decoding resumes
at each reached backslash.
Prepared terminal diagnostics admit their final buffer in the command workspace,
which holds it through later expansion, redirection setup and host writes.
Prepared help retains the reached builtin and admits its output after
descriptor setup. Shell-closed stdout bypasses that allocation.
Command-name and command-control preparation admit captured fields and
borrow the unexpanded operand tail from its existing source owner.
The builtin wrapper checks the target in the byte registry and decodes names
only after the registry accepts them.
Command queries build their output before writing. Output on one channel
keeps its end offset inline. Channel changes admit an ordered list of the
earlier offsets through the last write.
Raw `read` field splitting with up to three ASCII IFS bytes searches borrowed
record spans directly and skips building an IFS membership index.
If the host error output cannot be admitted, the evaluator reports a
short memory refusal through the reached stderr and continues the original
failure's status and cleanup path. A refusal uses the name `shell` when the
command name would make the short message exceed the bound.
Builtin operation errors share this formatter. The host detail stays borrowed
and is encoded directly into the admitted output buffer. The builtin retains
its own diagnostic destination, status and refusal handling.

Function definition rendering admits its pending work records, borrowed
here-document references and reusable long UTF-8 fragment buffer as scratch.
Scratch is refunded when rendering returns. The output owner retains the
rendered bytes through display, export or reparsing.

Do not recover by retrying, waiting, spilling, switching to streaming, evicting caches
or dropping optional storage. Failed allocation releases unused admission. Refusal
is distinct from cancellation and `OutOfMemoryException`; earlier effects remain,
without rollback or safe replay.

## Bash integration

`Engine.RunAsync` installs the host budget or a supplied evaluation owner.
`BashScript.RunAsync` owns its evaluation scope through descriptor cleanup; host
effects/output retain host owners. Retained evaluator state requires a longer-lived
caller-owned budget.

`EvaluationMemory` owns coarse estimates of command workspace and retained values.
Completed sibling commands reuse workspace; pending parents and overlapping
replacements stay charged. These reservations describe live ownership, with no
lifetime expansion credits. A nested host must receive a memory budget under the
same root; command inheritance does not copy or create a memory allowance.

Conditional evaluation admits its traversal buffer before growing it, including
overlap during replacement. Reached predicates reuse the buffer, which stays
charged through pending operations and is refunded when traversal unwinds.
`test` and `[` admit their parsed steps and file operands in the command workspace
before growing their backing lists. They admit reduction scratch before running
any predicates that require it. Ordered host request and response storage uses
the file lookup's own allowance through completion.
Case selection holds its expanded subject across reached alternatives and any
body chain that resumes matching. Each alternative keeps its expanded words and
quote metadata through comparison, then reuses the same command workspace for
the next alternative. Case alternatives and conditional `==` and `!=` compare
each expanded pattern once without retaining a prepared matcher. Pattern
scratch retires before a case body runs. When matching
will resume, compact subject backing keeps its charge and the expansion scratch
retires before the first body. The subject retires before the final body chain
or when matching ends. Retained binding effects survive workspace reuse.
The field splitter scans a single input span in place and uses reusable
scratch when it must decode adjacent spans together. Case fields can retain
slices of immutable input through the pattern workspace. Fields that use
the reusable scratch own their output bytes. The result list is admitted
before growth, and the field-assembly list retires when splitting finishes.

Command-prefix preparation and isolated external redirection expansion borrow
the caller's store and positional frame.
Only reached bindings acquire admitted, independent containers in command
workspace. Missing command bindings hide caller names without deleting caller
state. Element and array metadata reads use the visible slot directly. Reads
return immutable bytes or metadata, and writes take independent mutable backing
before publication. Aggregate values and keys can use the borrowed array when
the expansion consumes it before running another operand. Direct pattern
removal, replacement and case conversion also borrow when their operands have
no expansion markers. An operator that reads after operand effects keeps the
command-owned source. Array slices
with validated integer-literal bounds read from the borrowed source. A direct
computed slice borrows through numeric expressions that need no expansion or
binding. It detaches before the first reached bound that may consult bindings or
expand shell syntax. In-process installation admits the saved caller set before
publishing any prefix. It moves original slots to the saved store and consumes
the prepared prefix slots.
Retained outputs admit independent copies before staging, reusing
the surrounding command's capacity for later replacements. Valueless declaration
lookup admits its prefix selection buffer in command workspace as it grows.
Each operand clears the selected references before the next lookup.
Full child snapshots retain separate admission for their complete visible
store and saved function-local and prefix stores. Child context reconstruction admits
copies before allocating and reconnects enclosing frames iteratively. Mutable backing
is independent and immutable value bytes stay shared. The surrounding command owns
copied storage through operation and cleanup completion. Cancellation or refusal
during detachment leaves the caller binding intact.

Compound command prefixes keep expanded field groups through a single copy
into their parenthesized scalar value and refund the grouping list before
installing the binding.

`read` retains reusable line and logical record buffers within its size limit.
Custom-delimited record scratch stays charged through dispatch and normalization.
The field list and decoded fields share an allowance through assignment. Each
scratch allowance is refunded when its last consumer settles.

Directory stacks admit replacement backing before growth, and retained capacity
and owned path text stay charged across commands. Navigation admits normalized
path storage before construction, and stack mutations update the retained path
estimate as entries are replaced or removed. Child shell-state forks admit
independent stack and trap-map containers while borrowing immutable paths and
action bytes from the active parent. Inline subshells keep the saved stack and
handlers admitted through restoration. Directory output encodes directly into
one admitted byte array that survives the host's write. Descriptor snapshots
use the map writer's admitted capture headroom.

Local declaration checks for binding presence, attributes and nameref targets
read visible slots in their existing owner. Local declarations admit mutable
containers copied for inheritance or restoration. Unset can expose a saved
prefix by moving its owned slot into the visible store. Final prefix and local
restoration also move the selected slot, including
any retained output, after the last pending user settles. The retiring store
drops the slot and any superseded value. Child snapshots still own independent
mutable containers. The existing command workspace stays live through cleanup.
The active function chain maintains the total estimate for its saved local
stores. Mutating a saved store updates the total, including writes to an outer
hidden binding. A function or script prefix wrapper retires its contribution
when its scope ends. Each child context builds its own total from copied stores.

Ordinary element writes admit added positions and retained payload growth before
mutating an array. Indexed dictionary replacement admits overlapping backing
before resizing. Refusal leaves the existing entry or scalar binding intact.
Record input admits storage before updating the selected array. Non-integer
records own their normalized bytes independently, so replacing or removing an
entry retires its value estimate. The raw batch and offsets stay charged through
normalization and are refunded before the next batch. An explicit origin admits
uncovered dictionary replacement backing before applying any record. Integer
input retains its raw records through ordered arithmetic assignment and admits
uncovered growth at each reached write, using remaining batch credit and
holding the original binding through rollback.
`read` retains the normalized assignment prefix while consuming the complete
logical record. The physical read keeps its ordinary admitted buffer lifetime.
Custom-delimiter and counted reads normalize borrowed descriptor windows
directly into the reusable content buffer. Each successful window commits
only the accepted prefix. The window borrow settles before field assignment.

Tilde-prefix eligibility uses borrowed word metadata and bounded scalar state.
It does not detach a HOME/PWD/OLDPWD binding for an ineligible prefix. Reached
lookup and output retain their ordinary admission. The source cursor resumes
at the existing part and byte offset, preserving immutable arithmetic decisions
without allocating a suffix word or copying its remaining source.
Numeric directory-stack selection borrows the active stack. Encoding a saved
path uses the ordinary command workspace through expansion and consumption.

Suffix pattern traversal retains admitted atom offsets on the command-local
prepared pattern, with separate byte/scalar profiles when both are reached.
Subjects stay borrowed during matching; retained removal slices and changed
replacement output own their result bytes. Matching creates no reversed subject
copy or global pattern cache. Literal edge and replacement comparisons use byte
spans directly. Case conversion classifies the expanded pattern's encoding once
and matches it against decoded scalars. A present pattern with valid escaping
owns a fixed inline cache that reuses match results across its expansion fields.

Bracket terminal lookahead is stack-only state for the current match or atom
preparation pass. Small and unrevisited valid brackets use stack-only membership.
Revisited wide brackets prepare normalized membership within the current match.
The evaluator admits storage through its workspace and retires the temporary usage
on completion, refusal or cancellation. Suffix offsets retain their existing
command owner.

Pathname component ranges borrow the admitted pattern and quote mask. The command
admits the lookup and output forms of the literal prefix before creating them.
Pathname expansion admits both candidate lists and their path strings, releases
rejected candidates after lookup, and reuses list backing across components.
Sorting admits and orders the final path array directly. The sorted paths retain
their allowance until the caller finishes encoding them.

Immutable arithmetic metadata, literal word fields and prepared list navigation
belong to the syntax and are shared across executions. Stable operands and literal
fields borrow its bytes; runtime-expanded operands, selectors and argument
collections retain command workspace admission. A dynamic operand with a single
expanded segment and no prepared selectors reuses the segment's admitted bytes.
Other dynamic operands write joined fields directly into admitted backing.
Growth accounts for replacement overlap and the operand keeps its charge through
numeric evaluation.
An implicit `for` list shares fixed quoted positional syntax across parse owners.
The expanded argument snapshot belongs to each loop invocation.
Scalar assignment and byte-only selectors use the same field join to copy
expanded bytes and separators into their admitted result. Shell-pattern
consumers project bytes and quote masks directly from the fields. Joins for
regex translation and case-pattern splicing admit the final segment array in
the command workspace. The array borrows the already admitted bytes.
Parameter case conversion passes unchanged values through to field expansion.
Changed values join original byte ranges with the converted characters in an
admitted buffer. Parameter replacement expands both operands before matching. It
borrows literal replacement bytes and reserves any quoted escape encoding as
scratch from the first match through the last field. The escape transform reuses
already owned values when the scan finds no decoded escapes. Value transforms,
pattern removal and inspection forms
that quote individual fields stream their results into the word builder. Their
scratch admits generated backing while projecting and appending each field. The
builder independently admits the compact backing it keeps or the buffer it copies
into, which keeps emitted bytes charged through use.

Variable declaration rendering reads the visible slot synchronously in its
existing owner. It formats sparse indexed positions into reusable stack
storage before appending them. The rendered byte buffer has its own admission
through output or field consumption.

Aggregate operand lists admit replacement backing before growth and keep the
current backing charged through command completion. Byte consumers append
reached fields directly to that list.
Field splitting owns one reusable scratch buffer for pending bytes and one
quote-run list. It completes fields at their boundaries and reuses the segment
builder list. Word assembly can retain a compact immutable segment with a
command charge for its backing array, including when a binding is replaced.
Concatenation admits its new backing. Field splitting copies bytes out of its
reused scratch before publishing fields. Positional serialization publishes
a separate admitted snapshot for each quoted field. Non-scalar brace expansion
appends directly to the word's result. Brace scanning and branch materialization
reuse temporary storage and release it at brace expansion return. Generated word
parts and sequence bytes remain admitted through the caller's expansion and use.
Caller-prepared syntax remains
caller-owned. Runtime parsing supplies coarse admission to source copies, command-unit
tokens/parts and payload buffers, arithmetic/conditional metadata and reached deferred
substitutions. Runtime selector classification, parameter separator lookahead and
dynamic arithmetic reference scans carry the active workspace into nested syntax
preparation. Keyword, builtin and descriptor classification borrow source bytes;
stored loop/allocation names admit their text before decoding. Pure caller-owned
preparation remains unbudgeted. This is not complete allocation coverage; parser
structural guards remain independent of admission and execution duration.
An arithmetic operand consisting of one valid integer or only blanks retains its
value without token, reference or whitespace indexes. Other static operands admit
each lexical index on first use and retain it with the source owner.
Each running arithmetic parser admits its pending productions and traversal
backing before growth. Repeated productions at the same depth reuse this
allowance. It stays live through pending bindings and writes, including nested
numeric reads, and refunds once the parser settles.
Retained command lookahead includes the depth required by its tree. A nested
subshell can use an already prepared tree within the same syntax owner when the
receiving command and expansion depths have enough headroom.
Here-documents collected after a capture closes share the enclosing source's
syntax owner. The reader admits its physical body ranges, buffered line positions
and warnings before growing their backing lists. Streamed parsing releases the
range index with each command unit. Prepared evaluation keeps the completed
capture and warnings with their owning syntax.
When replaying a capture with an omitted separator changes the command
tree, the syntax owner keeps the already-admitted authored commands for
formatting. The parser keeps the replayed locations with the executable tree.
Literal payloads, assignment-name slices and completed expansion segments avoid
pinning disproportionately large builder buffers after their parsing/command
owner ends. An unchanged ANSI-C literal takes one exact copy of its payload.
Retained scalar, array, nameref and positional values keep the ordinary
value estimate; syntax slices share immutable arithmetic metadata without reparsing.
An unedited unquoted or double-quoted part takes one exact-sized copy. Edited
parts assemble their bytes in the admitted builder as their spelling changes.
Conditional display edits borrow the operands' decoded ANSI-C values and the
retained capture trees. The normalized display bytes have their own admission.
Structured here-document delimiters keep admitted compact spelling and closing
bytes with the parsed redirection, for collection and later function serialization.
Contiguous here-document bodies borrow their input source bytes. Tab removal or
continuation joining uses the existing syntax owner for normalized backing.
Here-document completion admits the traversal stack and node replacement map
under a temporary allowance on the active syntax budget. It includes overlapping
backing during growth and refunds the allowance before publishing the completed
unit. Source locations transfer after the traversal succeeds. The coarse syntax
owner continues to cover the resulting tree and body data.

A dispatched source read writes into the parser's admitted spare capacity.
A complete first batch that reports EOF can allocate its exact final buffer.
Other batches that need growth copy into the read batch owner, then grow the
parser's buffer after the host borrow settles. A host can refund its temporary
window on disposal, freeing capacity for the old and new parser buffers to
overlap. A known source length caps geometric growth when a batch requires a
larger buffer. A framed batch that exceeds the remaining parser limit returns
a size rejection before retaining payload bytes or offsets. The read batch
owns offsets for accepted input and releases them before the next batch or
descriptor close. Externally completed line operations use bounded aggregation.

A sourced input scope stays admitted while parsing/executing the file, while a
function namespace retains its definitions, and through active function preparation,
execution and cleanup. Shared definitions and attribute copies share this coarse
source owner. Replacement, unset and settled temporary namespaces release their
references; the last user refunds the scope. Prepared root namespaces remain owned
by the caller's evaluation scope. The source owner also admits the displayed path
used by failed redirection opens. Eval and trap admit one exact-sized backing
buffer from the expanded operands and separators. Eval, traps and array reader
callbacks retain the displayed path with their copied input. That independent
source owner leaves the caller's source buffer free to retire. Each command unit
has a coarse syntax owner
retaining its input owner. Completed units refund their metadata unless surviving
definitions or active users retain it; one early definition does not keep every
later unit's scratch alive. Reached backtick substitutions in stable source
reuse successful syntax under that source owner. This includes backticks reached
directly in a here-document body. The first parse admits retained syntax and the
cache entry. Concurrent first callers can parse independently and unpublished
candidates refund their reservations. Caller-prepared syntax uses a cache owned
by each run and shared by its forks. Deferred parsing of temporary operand
fragments uses storage through nested execution and cleanup. Function namespace
growth/copies are admitted separately from shared definitions and serialized exports.
Namespace keys follow the current definition's storage. Sorted function-name
snapshots admit compact copies. Queries that display function definitions retain
their selected source owners through host lookup and output completion.
Name, kind and path queries keep the selected classification.
Command queries borrow the expanded operands and admit their resolution records
and output offsets as a batch. Decoded host names, their list and the environment
snapshot remain admitted through the lookup. Request scratch retires before
formatting, which consumes host replies in the order of the marked operands.
After rendering, the bytes and output offsets stay admitted through writes.
The resolution records retire before the first output write. The selected
function-borrow list stays admitted through completion.

Serialized function exports share one admitted cache owner across namespaces and
attribute copies. Its scope belongs to the binding's creator, even when a pipeline
child performs the first serialization. Resolve that creator's installed budget
when publishing, because hosts can import definitions before Engine starts.
Concurrent first readers may render independently without waiting; unpublished or
refused candidates refund their own storage, and refusal does not retry a winner.
Replacement, unset and settled temporary namespaces retire their references.
Function calls, descriptive queries and host command payloads retain their selected bindings
through all consumers and descriptor cleanup; only the final owner refunds a cache.
The function namespace admits its ordered export selection on first use. It
borrows the selected bindings and refunds the selection when an exported binding
changes or the namespace settles. The command admits exact backing for its host
payload and retained bindings from the selection count before allocating either
array. Each invocation retains the reached bindings through dispatch and cleanup,
including the retained prefix when preparation fails.
In-process `exec -c` clears owned variable exports through the fork's export
index. An overlay admits a temporary snapshot of the visible export names before
detaching any borrowed slots. Function export clearing admits the exported
replacements as one batch before publishing their attributes and retiring the
cached selection. The batch retains the original bindings through replacement
execution. If execution unwinds, unchanged staged slots restore their original
bindings and completed edits retain their own state. The batch refunds after
publication or restoration settles.
Runtime environment snapshots admit result backing before growth. A merge
across saved scopes also admits selected-name scratch. Direct snapshots use
the unique names from the variable export index. Projection reads the visible
slot in its existing owner, including when an array masks an outer export.
Selection scratch retires before host dispatch. The result stays admitted
through the consuming lookup or invocation while borrowing the original name
and value bytes.
Preparing a hosted child with `CreateInherited` builds a caller-owned namespace
and positional frame. The engine installs the evaluation budget when it runs.
Retained root namespaces keep the caller-owned evaluation budget alive as before.
Hosts must admit independent copies of exported bytes retained after command completion.

`MemoryCapacity` amortizes admission with charged prepaid capacity. Syntax estimates
are coarse and do not account every small object. Public Parser calls, host-side
preparation/mutation and manual stepping do not install a runtime parsing budget.
Descriptor tables share immutable descriptor-to-slot, ordered-slot and handle-count trees.
Changed bindings copy frozen tree paths rather than whole maps. The active writer
reuses mutable builder nodes until capture freezes them; repeated
captures without a changed mapping reuse the completed snapshot. Before publication,
each receiving owner admits a coarse transitive slot estimate that covers all three
trees, reusable reference deltas/live-key backing, metadata and replacement
generations; existing owned headroom covers subsequent path replacement.
Borrowed snapshots retain their parent charge;
only a writing child acquires its own retained estimate. Preparation alone changes
no binding or ownership, so a later receiver's refusal leaves no unused tree to
retain. Saved redirection frames and in-process subshell parents stay admitted
through execution and cleanup. Estimates follow current slot backing, including
explicit closed mappings and vacant slots, rather than cumulative allocated nodes.
Captured sparse maps discard vacant-slot reuse at the former detachment boundary
and compact only when vacant slots outnumber live mappings, bounding abandoned
storage without traversing a live map on every capture/write. Compaction is selected
before admission; there is no smaller-reservation retry after refusal.
Reference counts are built alongside new bindings. Structural additions merge
pending changes before updating that basis. Replacements/removals accumulate bounded
overrides; capture merges them using already admitted headroom. Their keys are a subset of the base/current handle
union, bounded by twice the slot count. A dense live-key list permits removal and
merging without scanning retained dictionary buckets or vacant entries. Sparse
slot compaction also retires the old delta arrays. Snapshots share frozen counts;
writers retain their own deltas. Queries and cleanup neither build an index nor
request new admission. Inline stream references still use direct comparisons.
Saved-frame totals are maintained at mutation boundaries instead of rescanning on
every command retirement. Persistent publication prepares every receiving table
before changing that binding, and failed preservation leaves its scope active for
ordinary restoration/handle transfer. Earlier persistent allocations survive a
later setup refusal; its diagnostic uses the reached stderr before handles close.
Released and pending handle indexes preserve encounter order while reusing vacant
slots. Opened-handle and logical-descriptor bookkeeping prepares backing capacity
before growth, and cleanup tracking admits capacity before host acquisition.
Pending cleanup metadata has a coarse execution owner through Engine's final
awaited closes. Child handoff prepares the receiving index before publication;
refusal keeps the child owner alive while the parent's ordinary operation path
settles every close, then fails the enclosing command. An earlier exception or
cancellation keeps precedence. Stage stream lists already own their handles
through cleanup and do not acquire a new tracking reservation after execution.
Lexical and receiving redirection lifetimes share an ordered handle owner; setup
keeps its admitted frame installed through execution instead of allocating another.
Persistent bindings prepare the receiving owner and writable removal storage
before map publication; subsequent transfer/cleanup uses that prepared storage.
Prepared publication markers settle new handles in the transferring scope's order,
without duplicating older ownership or claiming borrowed subshell handles.
Forks borrow the handle index and admit independent storage on the next write.
Retained estimates include actual owned index capacity and saved frame/stack
storage. Final cleanup drops the owned indexes only after all close attempts have
settled, before retiring workspace. Ownership lists are no longer copied for every
successive transferred handle.
Here-string and here-document word assembly has a scratch allowance through
writer startup. Assembly returns unused credit before pipe acquisition. The
writer joins the expanded fields directly in its own admitted buffer. The
assembly allowance is refunded before the reader is published. Expansion
writes and descriptor bookkeeping keep their own owners.
When the host write settles, the writer discards the payload and returns its
allowance. Its completion channels stay admitted through endpoint settlement.
Here-input registration dictionaries prepare capacity before pipe acquisition,
including replacement overlap. Their actual high-water capacity belongs to the
execution cleanup owner, separately from producer payload/channel estimates.
Bounded inputs reuse this charged capacity; it survives final pending closes and
retires with execution cleanup after every user has settled.
The same execution owner prepares one ordered closing batch alongside pending
tracking, before handle acquisition or child cleanup handoff. Transfers, preserved
redirections and final evaluation/subshell cleanup reuse it without allocating
major containers during unwind. Pending ownership and existing subshell borrow
indexes select closes; cleanup no longer builds temporary exclusion indexes.
Completed batches clear only live entries, retaining charged capacity until final
execution cleanup. All close attempts settle before this storage retires.
Runtime available/borrowed collectors admit list/index growth and replacement
overlap through the parent command workspace. Parent commands retain this storage
through child execution, pipeline joins, host calls and cleanup; child workspace
retirement does not release it. Inline subshells reuse the completed collector's
membership index. Their stack capacity stays charged to the execution cleanup
owner through final users, then retires. The nearest entry already contains the
complete ancestor union, so collection does not revisit older entry sets.
Command substitutions and Bash command inheritance extend one admitted union
instead of copying and merging multiple lists. Caller-owned constructor inputs
stay borrowed. Public host-side CommandContext preparation remains caller-owned;
hosts admit independent copies they create or retain.
IFS membership snapshots belong to the variable scope that prepares them. Scalar
bucket/entry capacity and replacement overlap are admitted before growth; completed
capacity joins the variable owner's retained estimate. Child scopes can share
completed immutable indexes, but publish newly built indexes only in their own
cache slot. Borrowed command scopes account a replacement separately from their
parent's cache. Byte membership uses four inline words, independent of IFS length.
Default/empty membership, first-character lookup and unsplit joins need no index.
Refusal/cancellation publishes no partial index and follows ordinary cleanup;
there is no alternate matcher, smaller-reservation retry or recovery scan.
Coarse prepaid growth can reject even a small new index near an allowance boundary.

Descriptor reference queries use the writer's pending count overrides and frozen
count index, preserving aliases and explicit closed mappings without slot scans.
Descriptor/IFS admission does not imply complete evaluation accounting;
keep separate pipeline-preparation and other coarse-owner audits explicit.
Keep coverage limits explicit. Test shared allowances, refusal, immediate refunds,
retained results and cleanup with pending users.

Printf time conversion admits its temporary byte/character projection before
allocating it and reuses the conversion's rendering workspace. Literal bytes remain
borrowed from the original format until that conversion finishes. Result bytes are
copied into the ordinary admitted output builder and survive through its consumer;
the bounded strftime result rule does not turn an admission refusal into success.
