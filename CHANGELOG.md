# Changelog

## Unreleased

- Expose the active call stack through `BASH_SOURCE`, `BASH_LINENO` and
  `FUNCNAME`. In named files and inherited command strings, `caller`
  reports the call site's line and enclosing source, with the function
  name for indexed lookups. Unnamed input retains compact `0 name` output.

- Expose `BASH_ARGC` and `BASH_ARGV` under default shell options. The
  first top-level lookup snapshots the argument count and reversed
  positional arguments, including the arguments of a top-level source.

- Expose the live directory stack through `DIRSTACK`. Named reads see
  the current entries. Operand-free listings retain the last observed
  stack for the selected binding.

- Link `BASH_ARGV0` to the active shell name and expose the current
  subshell level as `BASH_SUBSHELL`. Preserve the original command bytes
  in `BASH_EXECUTION_STRING` for inherited `bash -c` launches.

- Initialize the exported `SHLVL` on new shell launches. Apply the
  reached launch-level adjustment for explicit and eligible implicit
  shell replacement.

- Accept explicit logical `cd -L`, including repeated options and grouped
  forms that select logical navigation. Accept `-e` without changing logical
  navigation.

- Search CDPATH for eligible relative directory operands and announce
  destinations selected by nonempty components.

- Recognize compound array initializers in quoted and expanded declaration
  operands. Expand the initializer after ordinary arguments and redirections.

- Preserve the final unmatched `help` topic and invalid option bytes in
  diagnostics. Return status 2 from `help --help` with shell-closed stdout.

- Use the shared help writer for `printf`, `read`, `mapfile` and `readarray`.
  Reached `--help` returns status 2, including with shell-closed stdout.

- Preserve Bash's DEL byte for later empty quoted aggregate fields in
  parameter replacement text.

- Use the first expanded field of a quoted aggregate in a case pattern when
  IFS enables field selection. Finish expanding the reached pattern before matching.

- Accept grouped `trap -p` options and report invalid flags through the reached
  stderr. Help and usage retain status 2 when their shell descriptors are closed.

- Report builtin listing write failures through the active stderr. Preserve
  successful directory-stack edits and caller selections when output fails.
  Support stack-only `pushd` and `popd` edits and signed stack indices.

- Publish foreground `PIPESTATUS` with Bash's binding, temporary-scope and
  negation rules.

- Apply parameter pattern removal to array entries after expanding the pattern.
  Skip the pattern for empty scalar values and aggregates with no fields.

- Preserve the reached positional and flow context when errexit runs an EXIT
  action, with Bash's binding, redirection and timing order.

- Use the same escaped-NUL and continuation rules for newline and custom
  delimiters in `read`.

- Format explicit printf timestamps across the pinned Linux calendar range,
  preserving Unix seconds and Linux year padding.

- Report ambiguous redirections through the active stderr mapping and preserve
  the expansion effects used to form the diagnostic.

- Preserve literal structured here-document delimiters through body collection,
  function display, and export.

- Reject capacity adjustments after their enclosing memory scope is disposed,
  including zero growth and same-size resizes.

- Support integer declarations and inherited locals with `declare -i` and
  `local -I`. Apply arithmetic evaluation to scalar and array writes.

- Support the `@a`, `@A`, `@K` and `@k` parameter transformations for attributes,
  reusable declarations and array key/value fields.

- Support bundled flags, required option arguments, and silent error handling in
  `getopts`. Track `OPTIND` across calls and set or unset `OPTARG` for each result.

- Support `read -a array` to replace an indexed array with all fields from
  one logical record.

- Support record limits and sparse array origins in `mapfile` and `readarray`
  through `-n count` and `-O origin`.

- Accept bare `+` while scanning `set` options. Support `type -p` for path output,
  `type -f` for lookup that skips shell functions, `type -P` for direct host lookup
  and `type --help` for the builtin synopsis.

- Support `$(<file)` and the corresponding backtick shorthand without a host
  command invocation. Preserve the caller's filename expansion effects.

- Support `mapfile -d delim` and `readarray -d delim`, including NUL delimiters
  and `-t` trimming of the selected delimiter.

- Support `local -` to save and restore the function's shell options.

- Apply quote and substitution rules across word fragments after a brace
  sequence generates a backslash or backtick.

- Retain a descriptor move's source close when an in-process command moves
  into a closed destination and the source has no earlier saved binding.

- Retain whole-name `read` assignments through local namerefs when a command
  prefix temporarily shadows the referent. Keep selectors in the active scope.

- Apply the implicit stderr duplication in `|&` after the left stage's
  explicit redirections. Preserve the reached stderr during setup.

- Expand braces across quoted, escaped and substitution fragments. Preserve
  literal outer braces around valid inner expansions and retain quote masks.

- Match signed decimal brace sequences, endpoint padding, step direction and
  zero increments. Retain ASCII character ranges and their empty backslash field.

- Report invalid for-loop variable names when the loop is reached and skip
  expanding its items. Incomplete loop syntax still fails during parsing.

- Diagnose readonly loop variables and invalid nameref targets in `for` loops.
  Preserve array elements when assigning the next item to element zero.

- Report `let: expression expected` when no expression fields reach `let`.

- Accept signed decimal counts and a leading `--` in `shift`. Preserve
  positional parameters on count and arity failures.

- Honor double-quote boundaries when an empty aggregate shares a word with
  other expansions. Preserve those boundaries in function display and export.

- Preserve an empty argument when a positional parameter such as `"$1"` is
  double quoted and unset.

- Reuse prepared literal command words when their fields need no pathname
  expansion.

- Reuse successfully parsed backtick substitution bodies across repeated calls
  while preserving deferred validation and fresh execution state.

- Support legacy `$[…]` arithmetic expansion with nested selectors and the
  existing arithmetic evaluator.

- Accept standalone braces in word positions, including loop items, case
  operands and redirections. Preserve brace-group delimiters.

- Preserve literal pathname prefix separators and restrict glob patterns ending
  in a slash to directories. Prepare each literal prefix once for traversal.

- Sort each word's pathname matches by UTF-8 byte order, including paths with
  both BMP and supplementary characters.

- Match supplementary Unicode characters as single characters in pathname
  wildcards and bracket expressions.

- Recognize command substitutions that begin with an adjacent subshell opener.
  Complete arithmetic expansions retain their usual precedence and diagnostics.

- Recognize locale-quoted `$"…"` words as double-quoted text in the invariant profile.

- Honor the leading `--` delimiter in `eval`, `source` and `.`. Validate options
  after field expansion and pass the selected file's remaining fields as arguments.

- Support `-v` and `-R` in `[[ … ]]`, `test` and `[` for assigned variables and
  initialized namerefs. Preserve selector expansion order and byte-valued keys.

- Expand colon-delimited tilde prefixes in assignment values and here-strings.
  Preserve quoting, array element roles and byte-valued paths.

- Expand `cd` operands as shell fields and reject surplus fields. Support HOME
  with no operand, OLDPWD with `-`, the `--` terminator and empty destinations.
  Print the selected OLDPWD spelling after a successful lookup.

- Expand numeric tilde prefixes from either end of the active directory stack.
  Preserve quoting and literal fallback for unavailable entries.
- Clear the stack silently for `dirs -c`, including when stdout is closed.

- Accept brace bodies in word and arithmetic `for` loops, optional opening
  parentheses in case arms and line breaks before case `in`. Reuse the existing
  loop and case execution paths and preserve function display and replay.

- Honor repeated and bundled leading `echo -n`, `-e` and `-E` options. Decode
  escapes into bytes and stop at `\c` when enabled. Skip writes for empty output.

- Index descriptor handle references with frozen alias counts and reusable writer
  deltas. Merge only live changes, preserving snapshots, closed mappings and alias
  cleanup without scanning every logical descriptor or retained vacant bucket.
  Admit index backing and capture/replacement overlap before publication.
- Share immutable descriptor lookup and ordered slot trees across snapshots.
  Copy changed paths instead of entire additional-descriptor maps, preserve
  encounter order and sparse-slot reuse, and prepare admission for every receiving
  owner before publication. Keep reference scans and host-handle cleanup separate.
- Admit IFS membership capacity before growth and retain completed indexes through
  their variable owner. Publish immutable snapshots in the active scope so child
  construction cannot grow a parent's cache. Keep byte membership in fixed inline
  storage, share completed scalar indexes, and preserve lazy joins and cancellation.
- Share named bracket atoms across shell pattern roles, including character
  classes, single-unit collating symbols/equivalence elements and suffix atom
  preparation. Preserve quotation, invalid-bracket literal fallback and the fixed
  byte/scalar profiles; keep regex classes distinct. Reuse closing/terminator
  lookahead instead of rescanning overlapping malformed bracket candidates.
- Parse conditional comments and closing delimiters in their operand context,
  preserving regex groups, physical error offsets and Bash's newline boundaries.
  Reuse nested substitution syntax across conditional scanning and word creation,
  removing exponential parse work. Keep function display/export reparsable and
  retain shared syntax admission and structural guards.
- Select complete tilde prefixes before variable lookup, preserving quotes,
  escapes and substitutions even when their value is empty. Keep quoted prefixes
  literal in words, assignments, patterns and paths; reuse source arithmetic
  metadata for reached suffixes through the existing expansion path.
- Preserve function-prefix values in first local declarations and reveal saved
  caller bindings after unset, including later caller writes and nested helpers.
  Keep declaration-command prefixes, local namespaces, attribute promotions and
  child forks distinct. Preserve structured temporary arrays in the documented
  nested inheritance profile rather than emitting GNU's observed byte 01 scalar.
- Admit runtime child-descriptor lists/indexes through parent command ownership,
  retain them through child cleanup and admit subshell stack capacity separately.
  Reuse completed indexes and one inheritance/substitution union; collect only
  the nearest complete ancestor entry instead of rescanning every older scope.
- Prepare one execution-owned closing batch before acquiring descriptors. Reuse
  it for transfer, preservation and final cleanup without late container growth
  or exclusion-index copies; clear consumed batches in work proportional to live
  entries while keeping their backing capacity admitted through cleanup.
- Admit here-input registration capacity before creating pipes. Reuse its charged
  high-water storage across bounded inputs and retain it through final awaited
  cleanup, independently of each producer's payload and completion channels.
- Admit receiving descriptor ownership before persistent map publication. Share
  one ordered owner through lexical setup/restoration, detach borrowed indexes
  before writes and reuse prepared storage during cleanup. Remove repeated growing
  ownership-list copies, preserve transfer/close order and retire handle indexes
  after final close attempts. Keep the admitted setup frame through execution.
- Index released and pending descriptors in encounter order with reusable slots.
  Admit bookkeeping growth before acquiring handles and keep pending metadata
  charged through final cleanup. Settle every child after a refused handoff while
  preserving earlier cancellation and exceptions through ordinary cleanup.
- Admit descriptor-map backing capacities and copy-on-write overlap. Keep borrowed
  snapshots and saved parents charged to their existing owners; retain actual map
  capacity after commands. Preserve earlier allocations on setup refusal and report
  through reached stderr before ordinary cleanup. Remaining temporary collectors,
  repeated copies/ownership scans and IFS indexes remain separate work.
- Admit runtime source copies and parsed-unit metadata, including retained
  arithmetic/conditional syntax, deferred substitutions and function namespace
  containers. Retire completed units independently, reuse sliced arithmetic
  metadata and keep retained values independent of oversized temporary buffers.
  EXIT-action parsing refusal now reports a shell failure through ordinary cleanup.
  Runtime selector/classification and separator scans share the active allowance,
  including nested syntax in unexpanded nameref targets. Classify ASCII grammar
  from borrowed bytes and admit retained text names before decoding.
  Caller-owned prepared parsing remains separate; IFS-index admission remains
  under review.
- Join unquoted here-document continuations before delimiter recognition and tab
  stripping. Preserve skipped command boundaries, quoted data, byte values and
  physical diagnostic offsets through the existing parser and execution paths.
- Refund cached function exports after their last namespace or active consumer
  retires. Charge first serialization to the binding creator, including pipeline
  publication, and keep borrowed definitions admitted through command cleanup.
- Preserve byte-valued printf time formats through the existing date renderer.
  Use ASCII width grammar and GNU's bounded strftime result before outer field
  formatting, retaining shared admission, cancellation and count-write ordering.
- Govern printf field widths and precision by actual output and admitted numeric
  workspace, removing the arbitrary 1 MiB cap. Bound exact numeric material and
  pad through the shared output builder with cancellation. Preserve alternate
  hexadecimal zero and the qualified GNU general-format carry forms.
- Parse printf numeric operands as bytes, preserving conversion diagnostics and
  character constants without CLR decoding failures. Bound integer accumulation,
  share extended floating-point parsing and admit its workspace with cancellation.
- Preserve directory bookkeeping failure status, eligible writes, namerefs,
  attributes and unassigned values after successful navigation. Keep partial stack
  changes and readonly-assignment EXIT timing through the shared cleanup path.
- Revalidate saved directory-stack destinations through the host before changing
  cwd. Preserve distinct failed pop/swap transitions and report failed or empty
  navigation through the shared directory-change path.
- Preserve legacy backtick spelling when displaying or exporting functions, so
  replay/import keeps deferred child-syntax validation, quoting and escapes.
- Reject self-referencing nameref declaration initializers at script scope before
  replacing values or attributes. Preserve function declaration warnings, global
  fallback for circular local references, and ordinary unset-nameref assignment.
  Share builtin output selection while retaining count and descriptor rules.
- Execute EXIT actions through the shared command-unit parser, preserving earlier
  effects before later syntax errors and stopping before unreached syntax after
  exit/errexit. Preserve trap-specific recovery/status rules and active scopes
  through explicit exit without adding another evaluator or public interface.
- Refund sourced input reservations after their last function binding or active
  invocation retires. Preserve shared definitions, child namespaces, attribute
  copies and pending cleanup; copied eval/trap syntax keeps separate provenance.
- Give escaped literal characters compact storage, like quoted literals. Retaining
  an escaped character from source/eval no longer pins the complete input buffer
  after its source reservation ends.
- Consume condition-level break/continue at their owning while/until loop.
  Preserve condition gating, prior body status and tested-status propagation
  through outer loops. Share outward-flow handling across all loop evaluators.
- Reject incomplete and malformed here-document substitutions when redirection
  setup is reached. Preserve prior effects, error status and cleanup while keeping
  quoted/skipped bodies and generated values inert.
- Recognize subshell bodies after `function name` without an empty `()` header.
  Require a compound body for both function-definition forms before executing
  their command unit, retaining prior effects and invocation-time redirections.
- Contain synchronous function-call execution depth without terminating the host
  process. Keep reached source/eval parsing independent of the caller's native
  stack headroom, preserving scopes, cancellation and descriptor cleanup.
- Expand here-strings as one scalar input without brace generation, repeated
  operand effects or discarded Cartesian products. Preserve backslashes before
  double quotes in unquoted here-document data.
- Reuse nested lookahead syntax while normalizing line continuations, avoiding
  exponential reparsing of mixed command/parameter expansions. Preserve physical
  source positions, source-slice boundaries and structural nesting guards.
- Require adjacent unquoted allocated-descriptor names and support append,
  read/write, force-output, duplicate/move and here-input operators through the
  existing redirection paths. Resolve nameref elements once, preserve temporary
  close/restoration rules and isolate external-command allocations from the caller.
- Resolve descriptor filename fallback once, support expanded close operands and
  source move suffixes, and diagnose invalid descriptor words as shell failures.
  Preserve quoted/expanded filename operands, lexical close suffixes, command
  restoration and cleanup without adding public redirection kinds.
- Complete here-document bodies through `time` wrappers, including timed compound
  commands and multiple documents sharing pipeline/list headers.
- Traverse flat pipelines iteratively in execution and display extraction, avoiding
  process-fatal stack overflow. Start here-input producers independently of their
  readers so bounded pipe backpressure cannot deadlock setup; retain admitted
  payload ownership through pending writes, persistent descriptors and cleanup.
- Compile generated benchmark wrappers against qualified Release binaries, keeping
  their identities stable through child builds. Add cross-runtime compile-only
  checks and preserve .NET 11 identity through the pinned harness's SDK validation.
- Add benchmark preparation: deterministic geometric workload
  catalog, bounded byte/digest host, controlled ownership/backpressure checks and
  managed/GNU correctness exports. CI verifies the managed harness without timing
  campaigns. Add guarded parse/prepared/public and matched process measurement
  lanes, startup-amortized variants, strict evidence gates, lifecycle previews and
  isolated all-thread allocation/GC observations. Add 36 direct core cases,
  Unicode/malformed-byte macro controls with reference-specific exclusions, and a
  separate Linux GNU-time RSS collector. The dedicated-VM campaign measured
  commit `c3139d7`.
  [Benchmark report](https://github.com/Lokad/Bash/blob/master/BENCHMARKS.md).
- Prepare 0.2.0 under the pre-1.0 minor-version API policy. Add isolated installed
  NuGet consumer qualification on Windows/Linux and .NET 10/.NET 11, with exact
  assembly checks and exported package/source/runtime evidence. No publication is
  part of this source and package qualification.
- Breaking API change: remove the unused standalone `ByteGlobMatcher`, its compiled
  `ByteGlobPattern` model, case/separator enums and token/range/segment types. Shell
  evaluation continues through the shared matcher. For application shell-word
  patterns, use `TextGlobMatcher.MatchUtf8` with an empty `GlobMaskSpan`,
  `requireEnd: true` and a cancellation token. It matches UTF-8 scalar values with
  malformed-input byte fallback; it does not replace the removed raw-byte,
  ASCII-insensitive or globstar pathname policies. Keep pathname policy with its
  application owner. This change follows the pre-1.0 minor-version API policy.
- Reuse immutable literal-word fields with their bytes and glob quote flags, while
  preserving dynamic expansions and pending failure/cancellation checks. Prepare
  command-list navigation once per syntax root instead of allocating a traversal
  stack on every execution. Keep execution state local, skip complete short-circuit
  subtrees and combine errexit positions with the current invocation context.
- Retain immutable arithmetic token decisions and split arithmetic-for clauses on
  source syntax, including ordinary arithmetic expansions. Stable operands without
  selectors borrow source bytes directly; runtime splicing and selector expansion
  stay late-bound. Preserve mutation/error ordering, short-circuit validation,
  stack-safe execution and independent bindings when a parsed script is reused.

- Share directional pattern traversal for suffix removal and replacement search,
  avoiding a fresh match for every candidate position. Reuse admitted bracket-atom
  offsets, search literals with byte spans, and retain input on no replacement.
  Preserve leftmost/longest selection, quoting, supplementary characters and
  GNU's malformed-byte profile across global replacement iterations.
- Reuse reached-binding detachment for external-command redirection expansion.
  Preserve ordered temporary writes and caller isolation without copying unrelated
  variables or arrays; literal redirection targets retain their direct path.
- Prepare command-prefix assignments by copying reached bindings instead of the
  entire variable store. Borrow positional frames, preserve RHS write forwarding,
  and admit independent containers before detachment. Keep full child snapshots
  isolated. Honor explicit prefix promotion through local and nested function
  restoration, including unset and redeclaration lifetime rules.
- Seek sparse indexed-array windows through the existing ordered position tree,
  avoiding scans over unrelated earlier entries. Preserve deferred reads, sparse
  order, count limits and empty/out-of-range behavior.
- Use owned positional argument frames with a head index for `shift`. Function
  and source scopes capture and restore caller frames without copying them;
  `set` copies its selected argument tail once. Preserve live read-only views,
  byte values, independent child state and source replacement rules.
- Publish global `BASH_REMATCH` captures, clear them on nonmatch and preserve them
  on invalid or unreached matching. Admit capture storage before replacing the
  prior binding. Parse direct POSIX bracket classes and support the twelve named
  classes with ASCII/C membership. Document remaining .NET regex grammar,
  longest-match, Unicode and cancellation departures explicitly.
- Execute complete command units through the same evaluator in `BashScript.RunAsync`,
  `source` and `eval`. Earlier units retain their effects before later syntax errors;
  an invalid unit causes no partial execution. Preserve explicit whole-script
  parsing/prepared execution, early exit and descriptor cleanup.
- Support implicit positional lists in `for name; do ...; done`, including newline
  and direct-`do` forms. Reuse quoted `"$@"` expansion and snapshot loop items before
  body changes to positional parameters.
- Validate byte-valued `exit`, `break` and `continue` operands with GNU status and
  flow distinctions, diagnostics, signed statuses, argument counts and EXIT scope.
- Publish `PIPESTATUS` for completed foreground commands and pipelines. Keep stage
  statuses before negation, preserve metadata through compound syntax, and replace
  it after the next command's argument expansion and completion.
- Parse conditional structure once, retain word quoting and expand only reached
  operands in scope. `[[` supports byte-valued string/pattern tests and genuine
  arithmetic comparisons, with GNU expansion order and short-circuit behavior.
  File checks occur in reached order; quoted/nested `]]` and regex brackets no
  longer close the command early. Regex dot includes LF.
- Breaking API changes: remove the scope-free `ConditionalEvaluation` helper;
  execute conditionals through `BashScript` or a parsed script and `Engine`.
  `ConditionalCommand` is an immutable class with parsed structure and a read-only
  `Expression`, rather than a positional record. Its source constructor validates
  structure; `Parser.TryParse` reports syntax errors without throwing.
- Preserve byte-valued `test`/`[` string operands. Apply argument-count precedence
  and strict signed decimal rules; malformed integers, missing `]` and syntax
  errors return status 2 with diagnostics. Share predicate metadata with `[[`.
  Option queries reflect supported `errexit` and fixed `pipefail` state.
- Preserve quote provenance and arbitrary shell bytes in case patterns and parameter
  prefix/suffix removal. Match valid UTF-8 characters without splitting surrogate
  pairs; use GNU's byte-matching fallback for malformed pattern/value bytes.
  Retained removal results own their byte storage independently of the input.
- Evaluate variable/array arithmetic values as expressions through the shared
  arithmetic evaluator, preserving dynamic bindings, side effects and short
  circuiting. Support octal, hexadecimal and base-2-through-64 constants with
  wrapping 64-bit results. Cyclic expressions fail through the existing structural
  arithmetic nesting guard rather than silently evaluating to zero.
- Add context-sensitive errexit through `set -e`/`set +e` and
  `set -o errexit`/`set +o errexit`, including tested contexts, function/source/eval
  propagation, pipeline failures, command-substitution inheritance and EXIT cleanup.
  Expose supported flags through `$-`/`${-}` and errexit inspection through
  `set -o`/`set +o`; reject unsupported option changes with a diagnostic.
- Remove cumulative execution quotas: long-running bounded scripts no longer spend
  lifetime word, character, brace, argument-copy or pattern-match credits. Keep
  cooperative cancellation, live-memory admission, structural parser guards and
  CLR representation checks.
- Breaking API changes: remove `CommandAllocationBudget`, `AllocationAllowance`,
  `ArgumentCopyAllowance`, argument-copy charge methods and rejection properties.
  `CommandContext` and `CommandInvocation.CreateWithInheritance` no longer take
  budgets or prepaid counts. Hosts own and admit their argument copies.
  `ShellEvaluationStartup.CreateForNewHost` no longer takes a parent invocation;
  use `BashScript.RunAsync(host, source, options, cancellationToken)` for new hosts.
  Pass a shared `IMemoryBudget` to nested hosts when they share a memory allowance.
  Pattern matching uses cancellation rather than a match-step count.

## 0.1.0

- Initial standalone Bash parser, operation-based evaluator and host contracts.
- Byte-valued language semantics, streaming pipelines, descriptor ownership and
  shared cooperative memory and expansion allowances.
- .NET 10 and .NET 11 targets, portable symbols and SourceLink.
- A bounded .NET regex match for conditional expression evaluation.

The initial API is pre-1.0.
