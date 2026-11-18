# Compatibility

The runtime supports deterministic, non-interactive shell workflows:

- Lists, `&&`, `||`, groups, subshells, functions, loops, conditionals and pipelines.
- Scalar, sparse indexed and associative arrays, namerefs and positional parameters.
- Quoting, ANSI-C quoting, brace/tilde/parameter/arithmetic/command expansion,
  word splitting, pathname expansion and quote removal.
- Redirections, here-documents, here-strings, logical descriptor aliases and
  allocated descriptors. `exec` can preserve descriptors or replace evaluation.
  Adjacent unquoted `{name}` supports file open modes, duplication/moves and
  here-inputs; variable closes follow ordinary command restoration. External
  command allocations and selector effects do not change caller bindings.
- Control, directory, variable, input and formatting builtins, including `read`,
  `mapfile`, `printf -v`, `source`, `eval`, `command`, `type`, `getopts` and `help`.
- `time`, host-mediated filesystem predicates, per-shell trap registration and EXIT
  handlers. Cancellation and external signals do not deliver traps.

Leading `~`, `~+` and `~-` use HOME, PWD and OLDPWD. The prefix must be unquoted.
An empty quote or escape inside it makes it ineligible. Quoting after a delimiting
slash leaves the prefix eligible. Expanded bytes never become new tilde syntax. Missing HOME
or OLDPWD leaves the spelling literal; missing PWD uses the actual current directory
for `~+`, an intentional departure from GNU. No account lookup is performed.
Numeric directory-stack forms `~N` and `~+N` count from the top starting at zero.
`~-N` counts from the bottom. Selecting the top uses the current shell value of
PWD, which can be empty. An unset PWD leaves a numeric top selection literal.
Other entries use their saved directory paths. Out-of-range numbers remain literal.

A literal unquoted colon starts another tilde prefix in scalar and direct array
assignment values, explicit indexed compound values, here-strings, and unquoted
assignment-shaped arguments such as `printf '%s' x=~:~`. Ordinary words and implicit
indexed compound values use only the leading prefix. For example, the ordinary
word `~:~` expands to the HOME value followed by literal `:~`. Associative compound
elements retain their existing literal tilde behavior. Quoted, escaped, or
expanded colons and here-document text do not start tilde prefixes.

Function-call prefixes such as `x=prefix f` supply the initial value of a first
local declaration, including through unprefixed helper calls. Unsetting that
temporary binding reveals the saved caller; subsequent assignments can survive
the call. Unsetting a local declaration instead keeps its unassigned local
namespace. A prefix on `local`/`declare` itself has a separate lifetime.
If a temporary scalar is converted to an array, a helper's new local inherits the
structured array value. This deliberately preserves its data and kind where both
pinned GNU 5.2 references emit a scalar containing byte 01 in that nested case.

Self-referencing nameref declaration initializers fail at script scope without
replacing the binding. Function declarations warn instead; circular local reads
and ordinary writes can select the global binding. A self-element target keeps
its separate selector semantics. Assigning an unset nameref may create a cycle,
and raw declaration inspection does not resolve that cycle.

Function display/export preserves legacy backticks and their original escapes.
Invalid child syntax in an unreached legacy substitution remains deferred when
the definition is replayed or imported; validation occurs when execution reaches
that substitution. Parenthesized substitution retains its existing parser rules.

Directory navigation revalidates saved stack paths through the host before
changing cwd. A rejected pop preserves the stack; a rejected no-argument push
replaces its second entry with the current directory, as in Bash. Failed lookup
preserves PWD/OLDPWD, and empty-stack operations diagnose failure.
`dirs -c` leaves only the current directory on the stack and emits no output.

`cd` accepts at most one expanded directory field after an optional `--`. Word
splitting and pathname expansion apply as for ordinary arguments. With no
field, `cd` uses HOME. A lone `-` uses OLDPWD and prints the selected spelling
once the destination is reached. Missing HOME or OLDPWD is an error when
selected. An empty destination revalidates the current directory. A spelling
such as `./-` is an ordinary path. CDPATH and the `-L`/`-P` modes are outside
the current path profile.

After successful navigation, directory bookkeeping writes OLDPWD before PWD,
preserving array kinds, namerefs and attributes. Readonly failures diagnose and
return 1 after changing cwd; eligible writes still occur, while push/pop completion
and stack output are suppressed. With errexit enabled, a readonly bookkeeping
failure exits immediately, including tested command positions, before remaining
writes or redirection restoration; EXIT sees the active scope and descriptors.
An unset PWD makes OLDPWD unassigned rather than assigning the prior actual cwd.
Present-but-unassigned array entries retain their keys/count and declaration shape;
aggregate expansion safely emits an empty field for them. This deliberately avoids
the native Bash crashes observed when aggregating those directory-created entries.

Source and shell values preserve bytes, including invalid UTF-8 values. Text-only
host boundaries validate UTF-8. Only LF terminates a source line; CR is data.
Locale-quoted words `$"…"` use their original text under the invariant profile.
They follow the same expansion and quoting rules as ordinary double-quoted words.
Command substitution removes NUL bytes and trailing LF bytes while retaining status.
An adjacent subshell opener can begin a command substitution, as in
`$((printf value);)`. A complete `$((…))` arithmetic shape selects arithmetic
even when its expression is invalid. The existing command grammar handles the
subshell form and consumes completed here-documents as part of its body.
Here-strings expand one scalar operand without brace generation, field splitting
or pathname expansion, then append LF. In unquoted here-documents, backslash escapes
dollar, backtick, backslash and LF; double quotes remain ordinary data. Quoting the
delimiter disables body expansion.
For unquoted delimiters, escaped LF is removed while collecting the body, before
delimiter recognition. Continued lines can form a delimiter; a physical line that
only resembles one inside continued data does not end the body. The `<<-` mode
removes leading tabs from logical lines rather than from every physical fragment.
Literal command-substitution spellings such as `$(...)` in delimiter words remain
outside the qualified delimiter profile.
Unquoted body substitution structure is validated when redirection setup is
reached; setup failure prevents target invocation. Quoted and skipped bodies
retain their inert data semantics.
File paths use the contained POSIX-style namespace defined by `ShellPath` and
`FilePathLookup`, including their size, control-character and traversal constraints.

IFS lookup follows the current scalar value through namerefs, including mutations
of their targets. Cached membership follows that value's byte-slice identity.
This deliberately retains consistent target lookup where the two pinned GNU 5.2
builds give different stale-cache results for IFS nameref-target changes.
Valid input characters use scalar membership; malformed input bytes test every
configured IFS byte, including bytes inside otherwise valid UTF-8 separators.
Only space, tab and LF have IFS whitespace semantics.

The deterministic character profile is UTF-8 with invariant character operations.
Length and substring operations count Unicode scalar values and individual malformed
bytes. Shell-word patterns preserve quoting and match scalars when both operands
are valid UTF-8; a malformed pattern or value selects byte matching, as in GNU's
UTF-8 locale. Removal slices the selected bytes directly and can therefore produce
malformed byte values without passing through a text encoder. Pathname operations
retain their separate text-only host boundary and path component policies.

Shell bracket expressions recognize `alnum`, `alpha`, `ascii`, `blank`, `cntrl`,
`digit`, `graph`, `lower`, `print`, `punct`, `space`, `upper`, `word` and `xdigit`.
Membership is fixed ASCII/C, including when matching UTF-8 scalars: non-ASCII
letters do not become members of `alpha`. Negation, unions and ranges retain
their ordinary bracket roles. Complete unknown classes have no members.
Collating symbols `[[.c.]]` and equivalence elements `[[=c=]]` select one unquoted
matching unit with exact identity, without locale weights or multi-unit symbols.
Malformed/quoted atom spellings retain their distinct literal-fallback rules.
The byte APIs use the operand's scalar/byte profile. Pathname matching treats a
valid UTF-16 surrogate pair as one character within each component. Emitted paths
for each expanded word are sorted by unsigned UTF-8 byte order, independent of
locale. Public text matcher overloads count UTF-16 code units.

Pathname expansion preserves separators in the literal prefix before the first
wildcard component. It joins subsequent components with a single slash. Trailing
slashes restrict matches to directories, including links to directories, and emit
one final slash. A pattern with no match retains its original spelling.

Replacement chooses the leftmost, longest match. A global replacement applies
the character profile to each remaining operand: after consuming malformed bytes
it can resume scalar matching, while a remainder beginning inside a multibyte
character continues in byte mode. Candidate positions within one search share
that operand's profile. Suffix replacement uses the complete operand's profile.

Pipeline status uses fixed `pipefail`: the rightmost failing stage wins. The
indexed `PIPESTATUS` array exposes individual foreground statuses.
The array records statuses before pipeline negation; a subsequent command expands
its operands against the previous array and replaces it when it completes. Empty
loops and function definitions preserve the previous array.
Scripts are limited to 4 MiB, with structural syntax/nesting guards and CLR collection-size
checks. The source bound limits parser input; nesting guards protect recursive
syntax handling. They are independent of execution duration. There are no lifetime
word, character, brace, argument-copy or pattern-step quotas. Callers supply
cancellation and a live-memory allowance. Here-document bodies are collected in
physical source order across pipeline/list headers and completed through timing,
negation and compound-command wrappers. An incomplete body is rejected rather
than accepted with GNU's end-of-input warning. Builtins implement their documented option subsets;
`set` sets positional parameters and supports `-e`/`+e` and
`-o errexit`/`+o errexit`. Errexit stops unhandled failures, including failed
pipelines under the fixed pipefail policy. Tested conditions, logical-list
positions and negation suppress it through nested functions/groups and source/eval.
Command substitution clears errexit by default; parenthesized subshells inherit it.
`$-` and `${-}` expose the supported mutable flags. `set -o`/`set +o` inspect
errexit only; other option changes return an error with a diagnostic.

`echo` consumes leading arguments made of a dash followed by one or more `n`
bytes and omits the final LF if any such argument is consumed. The first other
argument, including `--` or an empty argument, ends option recognition. Arguments
are joined with one space and backslashes remain literal.

Regular expression conditions use .NET regular expressions with quote-aware pattern
escaping and dot matching LF. They require valid UTF-8. Named bracket classes
`alnum`, `alpha`, `blank`, `cntrl`, `digit`, `graph`, `lower`, `print`, `punct`,
`space`, `upper` and `xdigit` use ASCII/C membership, including in direct pattern
syntax. Unknown classes and collating/equivalence elements return status 2.

Successful matches replace global indexed `BASH_REMATCH` with the complete match
and numbered captures; unmatched groups contain empty values. A nonmatch clears
the array. Invalid, unsupported or unreached matching preserves prior captures.
Publication replaces prior binding kind and attributes, without following namerefs;
a function-local shadow remains local while matching updates the global binding.

This is a qualified .NET regex profile, not full POSIX ERE compatibility. Alternation
uses .NET ordering rather than POSIX leftmost-longest selection; `$` can match before
a final LF. Quoted bracket members follow .NET escaping, with departures from GNU's
bracket rules. Named classes do not gain Unicode locale membership. Matching uses
UTF-16 code units; a capture splitting a supplementary character returns status 2
instead of publishing malformed encoded text. Each match has a 250 ms backtracking
timeout, also returning status 2. Caller cancellation is checked around compilation,
matching and capture publication, but cannot interrupt an individual regex call;
compilation itself is not covered by the match timeout.

`[[` retains parsed structure and operand quoting. Reached operands undergo normal
parameter, command, arithmetic and tilde expansion, without brace generation,
field splitting or pathname expansion. Equality uses shell patterns with quoted
pieces treated literally; numeric predicates evaluate arithmetic expressions after
both operand words expand. Logical operators skip unreached expansions and host
queries. Each reached file predicate observes its own host result. Invalid
conditional structure, including an empty `[[ ]]`, is a syntax error.
Token-start comments apply inside `[[` and end only at LF. Delimiter-looking bytes
inside regex groups remain operand data. LF is accepted before a term or after
a complete predicate; it cannot split an operator from its required operand, or
follow the left word before predicate selection. Backslash/LF continuation retains
its ordinary lexical rules. Comments do not execute expansions or terminate early
on quotes, `]]` or CR.

Extended shell patterns `?(...)`, `*(...)`, `+(...)`, `@(...)` and `!(...)` are
unsupported in `[[`, case arms and parameter pattern operators. Case allows line
breaks between the subject and `in`, an optional opening `(` before each arm's
patterns and the `;;` terminator. Fallthrough `;&` and retesting `;;&` remain
unsupported.

Word and arithmetic `for` loops accept a brace body as well as `do ... done`.
A word loop needs a semicolon or line break before the opening brace. An
arithmetic loop can place the opening brace immediately after `))`.

Out of scope: interactive prompts/history/completion, aliases, background execution,
job/process-group control, OS signal handling, process substitution, `coproc`,
`select`, network redirection devices, `ulimit`, `umask` and shell option toggles
other than errexit.
The runtime does not import process environment variables or startup files.

Both function-definition forms require a compound command body. In `function name`,
the optional empty `()` header is distinguished from a subshell body beginning with
`(`. Function-body redirections apply when the function is invoked.

`getopts` currently supports separate flag arguments and advances `OPTIND` over
the active positional window or explicit arguments. Bundled flags, required option
arguments, `OPTARG` lifecycle and GNU diagnostic/silent modes remain unsupported.
The correctness corpus records the required-argument departure explicitly.

The public script runner, `source`, `eval` and reached EXIT actions execute complete command units as
they are parsed. Earlier units retain their effects before later syntax failure;
the failing unit executes no partial command list. Explicit whole-script parsing
remains available for callers that require validation before execution.

`eval`, `source` and `.` consume one leading `--` from their expanded fields.
Other multi-byte dash options report status 2. A leading `--help` prints the
builtin's usage and returns 2. Later fields are ordinary operands.
`eval` joins its remaining fields with spaces and succeeds on empty input.
`source` and `.` require a filename and use any following fields as positional
parameters for that file. The filename must be valid UTF-8 for the host path
boundary. Script content and positional parameters retain their byte values.

EXIT actions preserve the triggering status on ordinary completion and parse failure
without errexit. Explicit return/exit and errexit failures can replace that status.
Arithmetic/default expansion failures end their unit; assignment-target and parameter
failures stop an errexit-enabled action. Pending loop flow skips later execution while
still validating remaining units. Caller tested-status suppression does not apply to EXIT.

Arithmetic uses wrapping signed 64-bit values, with decimal, octal, hexadecimal and
explicit base-2-through-64 constants. Stored variable/array values are arithmetic
expressions evaluated against current bindings; they are not re-expanded as shell
source. Active numeric recursion shares the structural nesting guard and observes
cancellation. Completed arithmetic work consumes no execution allowance.

`test`/`[` preserve bytes in string equality, ordering and length predicates. Their
zero-to-four argument rules precede the `!`, parenthesis, `-a` and `-o` expression
grammar. Numeric operands must be signed 64-bit decimal integers, with optional
surrounding ASCII whitespace; they do not evaluate arithmetic expressions or base
prefixes. Invalid integers, malformed expressions and a missing closing `]` report
a diagnostic and status 2. `-o` queries recognize `errexit` and the fixed `pipefail`
policy; unknown option names are false. File predicates retain the UTF-8 path boundary.

`[[ … ]]`, `test` and `[` recognize `-v` and `-R`. An assigned empty value counts as
set. `-v` accepts variable names, array selectors and numeric positional parameters.
A bare array name queries element zero. For indexed arrays, `[@]` and `[*]` query
element presence across the array. For associative arrays, they denote the literal
keys `@` and `*`. `-R` tests whether the named binding is an initialized nameref,
including a reference to an unset target.

Printf numeric operands use byte-oriented C-locale prefix parsing. Invalid suffixes
retain the converted prefix, report a byte-preserving diagnostic and return status 1;
range warnings preserve their successful conversion status. Character constants
consume one UTF-8 scalar or one malformed byte after the quote. Floating-point
conversion uses the deterministic 64-bit-significand extended profile, including
the pinned Linux hexadecimal spelling and NaN/subnormal behavior. Exact subnormals
do not produce range warnings. A shell-closed stderr discards conversion diagnostics
without suppressing stdout; host-reported diagnostic failures retain their ordinary
stop policy. Numeric parsing observes cancellation and shared workspace admission.

Printf width and precision have no independent 1 MiB quota. A large precision that
only truncates a short string or leaves a short general-format result does not
require proportionally large workspace. Actual output, replacement overlap and
numeric material must fit the supplied live-memory allowance and CLR representation
limits. The deterministic Linux profile includes the alternate-form decimal point
for hexadecimal zero and discarded fractional zeros when a general-format carry
changes fixed notation to scientific notation.

Printf time-format literals preserve bytes, including malformed UTF-8. Directives
and widths use ASCII grammar with invariant date text; unknown directives follow
the pinned Linux strftime spelling. Each `%(...)T` conversion uses GNU's 128-byte
buffer profile: a result requiring 128 or more bytes becomes an empty value with
successful status. Outer field width and precision apply afterward. This conversion
rule is distinct from shared-memory refusal, which remains a reported failure.

Implicit `for name; do ...; done` iterates a snapshot of quoted positional
parameters, preserving empty arguments and bytes. Explicit `in` lists retain their
usual expansion rules. `exit` accepts signed 64-bit decimal statuses modulo 256;
invalid numbers and surplus arguments diagnose and stop non-interactive execution.
Outside a loop, `break`/`continue` warn and return 0. Level-one condition flow is
consumed by the current while/until loop after its condition gate is applied.
Higher counts still select outer loops; functions retain their own loop boundary.
Within loops, nonpositive counts abandon all active loops and the builtin reports
status 1; malformed counts and surplus operands have
their distinct shell-exit statuses (128 and 1).

Hosts provide external commands, including any `sh`/`bash` aliases and script-file
launch policy. Command names and filesystem capabilities are never inferred from
the ambient machine. Additional host tools do not expand the language grammar.
