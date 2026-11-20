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

`mapfile` and `readarray` split standard input on LF by default. `-d delim`
uses the first byte of `delim`, with an empty operand selecting NUL. `-t`
removes a record's final delimiter when present. Array values end at the first
NUL byte, including when NUL is the delimiter.
`-n count` reads at most that many records and leaves the next record on the
descriptor. Zero reads to EOF. `-O origin` overwrites consecutive entries
starting at the supplied index and preserves the other entries. Both operands
accept unsigned 32-bit decimal values. Destination indices continue from zero
after index 4294967295.

`read -a array` replaces an indexed array with all fields from one logical
record using IFS and the ordinary delimiter and escape rules. It assigns an
empty array for a record with no fields. At EOF it publishes fields
from the partial record, or an empty array, and returns failure.

`read` skips unquoted NUL bytes unless NUL is the chosen delimiter. With
escape processing enabled, a quoted NUL ends the text available for assignment
and counts as a protected empty field when it occurs on its own. The read still
consumes the logical record, including escaped delimiters. If Bash 5.2 emits
its internal `0x01` marker for that empty field, Lokad.Bash assigns empty text.
Literal `0x01` input remains data. With `-r`, backslashes remain literal.

`$(<file)` and its backtick form read a single input-file redirection on
descriptor zero. Filename expansion runs in the caller. The result removes
NUL bytes with a diagnostic and strips trailing LF bytes. CR is preserved.
Input failures reported by the host retain their failure status.

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

Variable-prefix expansions `${!prefix@}` and `${!prefix*}` include assigned
names from enclosing function scopes. An unassigned local can hide a value
while the outer assigned name still appears in the listing. Matching names
appear once in unsigned-byte order. Ordinary parameter reads follow the
active binding, and quoted `*` uses the ordinary IFS join rules.

Self-referencing nameref declaration initializers fail at script scope without
replacing the binding. Function declarations warn instead; circular local reads
and ordinary writes can select the global binding. A self-element target keeps
its separate selector semantics. Assigning an unset nameref may create a cycle,
and raw declaration inspection does not resolve that cycle.

Function display/export preserves legacy backticks and their original escapes.
Invalid child syntax in an unreached legacy substitution remains deferred when
the definition is replayed or imported; validation occurs when execution reaches
that substitution. Earlier complete units in a backtick capture can emit output
or create files before a later syntax failure. Ordinary child syntax failure
returns status 2. A terminating nested substitution grammar error returns
status 1. EXIT in the capture sees that status and can override it.
Parenthesized substitution retains its existing parser rules.

Directory navigation revalidates saved stack paths through the host before
changing cwd. A rejected pop preserves the stack; a rejected no-argument push
replaces its second entry with the current directory, as in Bash. Failed lookup
preserves PWD/OLDPWD, and empty-stack operations diagnose failure.
`dirs -c` leaves only the current directory on the stack and emits no output.
`pushd` and `popd` accept signed stack indices and `-n` for edits that
retain the working directory and its variables. A directory stored by
`pushd -n dir` retains its supplied bytes until a later navigation resolves
the path through the host. Successful stack edits retain success if the
listing write fails, and the write cause is reported through active stderr.
`pwd`, `dirs`, and declaration and option listings report write failures
and return failure. `caller` uses a compact function-context display and
returns the frame selection status even if the output write fails.

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
Standalone unquoted braces are words in operand positions, including command
arguments, loop items, case subjects and patterns, array entries and redirection
operands. Brace groups require a semicolon or LF before their closing delimiter.
Brace expansion can join alternatives across quoted or escaped fragments and
substitutions. Protected commas are data and alternatives retain their original
quoting through splitting and pathname expansion. A backslash or backtick generated
by a character range can change how subsequent quotes and substitutions are read.
An incomplete generated substitution fails when that branch reaches expansion.
Brace sequences accept signed 64-bit decimal endpoints or single ASCII letters.
The endpoints choose direction and a zero step selects unit magnitude. Leading
zeros on unprefixed or negative endpoints request padding to the wider endpoint,
including its sign. The pinned Bash 5.2 profile formats padded results as signed
32-bit values after computing the sequence in 64 bits. Empty or overflowing
increments remain literal.
Command substitution removes NUL bytes and trailing LF bytes while retaining status.
An adjacent subshell opener can begin a command substitution, as in
`$((printf value);)`. A complete `$((…))` arithmetic shape selects arithmetic
even when its expression is invalid. The existing command grammar handles the
subshell form and consumes completed here-documents as part of its body.
If that grammar rejects a balanced adjacent form, the capture reads its source
in complete units when reached. A failing child unit reports its diagnostic and
leaves any earlier complete units' output and effects available. A skipped
capture stays inert.
A diagnosed command substitution grammar error in `source` or `eval` can
terminate the invoking shell even in a tested command. A malformed arithmetic
`for` header in an unquoted substitution terminates with status 2. With an
enclosing double quote, the builtin returns the status of any complete command
prefix in that unit, or 2 if the unit has no complete prefix. A compound array
assignment with that quoted error returns 1. For errors such as a pipeline
with a missing command, EXIT sees parser status 257 and shell completion
returns its low byte, 1. In a command capture or a simple pipeline stage,
the child unwinds the builtin and completes with status 1. Ordinary syntax
errors and incomplete substitutions continue to use the builtin's return path.
A final bare `time` in an affected parenthesized substitution reports a syntax
error when the substitution is reached, before executing any of its commands.
The enclosing command unit ends with status 1 and earlier bindings retain their
values. The failure ends the script with status 2 under errexit. An invalid nested
spelling in an ordinary rendered substitution also ends the script with status 2.
An adjacent subshell opener retains its source separators, and a nested spelling
failure there ends the unit under ordinary execution. Function display and export
retain the original timing separator so reparsing a definition preserves the
point of validation.
Parenthesized capture rendering can omit a semicolon after a flushed
here-document. The resulting command list can combine operands or attach a
redirection to the preceding command. If the rendered list fails to parse,
the reached substitution reports a syntax error before any of its commands
run. Backtick captures and captures read directly from a here-document use
their source separators.
Legacy `$[expression]` uses the same arithmetic evaluation as `$((expression))`,
with a single closing bracket outside nested brackets. Within a double-quoted
word, an unescaped inner double quote at that level causes a substitution failure
when the bracketed expansion is reached. Both forms preserve a quoted bracket in
an associative array key, including `a["]"]` in an unquoted arithmetic expansion.
Both pinned GNU builds report an arithmetic error for that unquoted key.
Here-strings expand one scalar operand without brace generation, field splitting
or pathname expansion, then append LF. In unquoted here-documents, backslash escapes
dollar, backtick, backslash and LF; double quotes remain ordinary data. Quoting the
delimiter disables body expansion.
For unquoted delimiters, escaped LF is removed while collecting the body, before
delimiter recognition. Continued lines can form a delimiter; a physical line that
only resembles one inside continued data does not end the body. The `<<-` mode
removes leading tabs from logical lines rather than from every physical fragment.
Structured spellings such as `$(...)`, backticks, arithmetic and braced
parameters provide literal delimiter text. Quoting any part of the delimiter word
applies quote removal to the whole word and disables expansion of the body.
Nested source retains its lexical spelling after continuation and quote-prefix
processing. Function display and export keep the opener and matching closer.
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
Pipelines record stage statuses before negation. A subsequent command expands
its operands against the previous statuses. Publication preserves scalar,
associative, and declaration-only bindings. Initialized sparse arrays reuse their
positions when they fit the incoming status list. Empty loops and function
definitions preserve the previous array.
For `|&`, the left stage applies an implicit `2>&1` after its explicit
redirections. Setup diagnostics use the stderr mapping reached so far.
After an ambiguous redirection, the evaluator expands the operand again to name
it in the diagnostic. Direct command substitutions remain text in that pass.
Arithmetic and braced parameter operands keep their own expansion behavior. If
the pass produces zero or multiple fields, the diagnostic uses the operand's
spelling. A descriptor move closes the destination when the operand produces
zero or multiple fields. A single quoted empty field reports a bad descriptor.
Function-body redirections apply on invocation inside the completed stage setup.
Scripts are limited to 4 MiB, with structural syntax/nesting guards and CLR collection-size
checks. The source bound limits parser input; nesting guards protect recursive
syntax handling. They are independent of execution duration. There are no lifetime
word, character, brace, argument-copy or pattern-step quotas. Callers supply
cancellation and a live-memory allowance. Here-document bodies are collected in
physical source order across pipeline/list headers and completed through timing,
negation and compound-command wrappers. An incomplete body is rejected rather
than accepted with GNU's end-of-input warning. Builtins implement their documented option subsets;
`set` sets positional parameters and supports `-e`/`+e` and
`-o errexit`/`+o errexit`. A bare `+` continues the option scan. A bare `-` ends
the scan, with following operands becoming positional parameters. `--` replaces
the parameters even when no operands follow. Errexit stops unhandled failures,
including failed pipelines under the fixed pipefail policy. Tested conditions, logical-list
positions and negation suppress it through nested functions/groups and source/eval.
Command substitution clears errexit by default; parenthesized subshells inherit it.
`$-` and `${-}` expose the supported mutable flags. `set -o`/`set +o` inspect
errexit only; other option changes return an error with a diagnostic.
`$_` holds the last expanded argument of a completed simple command. A command
without arguments records its name, and a command with only assignments or
redirections records an empty value. Word and redirection expansion can read the
previous value. Function calls publish their invocation argument on return.
Subshell and pipeline commands keep their own updates. The binding preserves
bytes and follows ordinary variable assignment rules, including readonly
diagnostics and local bindings.
Inside a function, `local -` saves the active mutable option state. The function
restores it on return. A second `local -` keeps the first snapshot, as in the
pinned Bash 5.2.37 build. `local -p -` inspects the current function's snapshot.

`type -p` prints the host's reported path when the selected name resolves to an
external command. Builtins, keywords and functions resolve successfully in this
mode and produce no output. `type -f` skips functions during lookup and combines
with `-p` or `-t`. The last `-p` or `-t` selects the output mode.

`shift` uses expanded fields and accepts a leading `--`. Its count defaults to
one. Explicit counts accept signed 64-bit ASCII decimal with leading ASCII
whitespace and trailing space or tab. A count larger than the remaining
positional window returns 1 silently. Negative or invalid counts return 1 with
a diagnostic. A valid count followed by any surplus field diagnoses the arity
error and exits the current shell evaluation with status 1.

`echo` consumes leading arguments made of a dash followed by one or more `n`,
`e` or `E` bytes. Any consumed `n` omits the final LF. Backslashes are literal
by default. The last consumed `e` or `E` enables or disables escape processing.
The first argument outside that grammar, including `--` or an empty argument,
ends option recognition and becomes the first output operand.

`echo -e` decodes `\\`, `\a`, `\b`, `\e`, `\E`, `\f`, `\n`, `\r`, `\t`,
`\v`, `\0` with up to three following octal digits, and `\x` with one or two
hexadecimal digits. `\u` and `\U` take up to four or eight hexadecimal digits,
respectively, and use Bash's extended UTF-8 encoding independently of locale.
Unknown escapes and hexadecimal escapes without digits remain literal. Decoded
NUL is written as an output byte. `\c` ends output at that point, omitting the
final LF. Arguments expand before formatting and are separated by a single space.

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
Unquoted `<` and `>` are order operators in `[[`. Quoting or escaping them
keeps them as operand bytes. A plain decimal within the descriptor range
directly before either operator has descriptor syntax and is rejected there.
Token-start comments apply inside `[[` and end only at LF. Delimiter-looking bytes
inside balanced regex groups remain operand data, including unquoted spaces,
LF and `#`. Shell quoting and escapes govern parenthesis balancing even within
a regex bracket expression. At the top level of a regex word, unquoted `|` is
alternation data and `&&` ends the word and resumes conditional parsing. An
empty regex word before `&&` or a conditional `)` uses the .NET regex profile.
LF is accepted before a term or after a complete predicate. It cannot split
an operator from its required operand or
follow the left word before predicate selection. Backslash/LF continuation retains
its ordinary lexical rules. Comments do not execute expansions or terminate early
on quotes, `]]` or CR.

Extended shell patterns `?(...)`, `*(...)`, `+(...)`, `@(...)` and `!(...)` are
unsupported in `[[`, case arms and parameter pattern operators. Case allows line
breaks between the subject and `in`, an optional opening `(` before each arm's
patterns and the `;;` terminator. Fallthrough `;&` and retesting `;;&` remain
unsupported. Inside an explicitly parenthesized arm, an unquoted `esac` can be
a literal pattern. Lokad.Bash applies that grammar inside command substitutions
and captures the selected arm's output and status. In the recorded conformance
case, both GNU Bash pins produce text from the arm's source inside the capture.

Quoted `$@` and `${array[@]}` in a case subject join fields with spaces. An
empty field contributes an empty payload, and a literal DEL byte (`0x7F`)
remains data. In the recorded multi-field comparisons, both GNU Bash pins
select the same DEL-containing pattern for an empty field and a field that
contains DEL.

Word and arithmetic `for` loops accept a brace body as well as `do ... done`.
A word loop needs a semicolon or line break before the opening brace. An
arithmetic loop can place the opening brace immediately after `))`.
An arithmetic `for` header requires exactly two unquoted semicolons outside
shell expansions. A malformed separator count is a syntax error before any
clause or loop body runs. An empty condition or one containing only spaces
and tabs is true. If the condition contains only spaces, tabs and one or
more physical LFs, it evaluates to zero. Backslash-LF continuations are removed first.

A complete compound command can be followed directly by a closing brace or a
keyword such as `then`, `do`, `fi`, `done` or `esac`. The last command in a list
or pipeline supplies that boundary. A simple command or a trailing redirection
needs a separator before these tokens.

Out of scope: interactive prompts/history/completion, aliases, background execution,
job/process-group control, OS signal handling, process substitution, `coproc`,
`select`, network redirection devices, `ulimit`, `umask` and shell option toggles
other than errexit.
The runtime does not import process environment variables or startup files.

Both function-definition forms require a compound command body. In `function name`,
the optional empty `()` header is distinguished from a subshell body beginning with
`(`. Function-body redirections apply when the function is invoked.

`getopts` scans explicit arguments or the current positional parameters one option
byte at a time, including bundled flags. A colon after an option byte requires an
argument. The attached remainder or the next argument is assigned to `OPTARG`,
including an empty argument. A leading colon selects silent errors. `OPTERR=0`
suppresses diagnostics while retaining the normal error results.
An explicit `OPTIND=1` restarts the scan. A visible `OPTIND` can stay unchanged
between bytes of a bundle. The scanner stops at `--` or the first non-option and
reports status 1. `OPTIND` starts with the integer attribute.

`declare -i` and `local -i` evaluate subsequent assigned values as arithmetic
and store the decimal result. For integer bindings, `+=` adds the evaluated
values. `+i` clears the attribute. A command prefix drops the integer attribute.
Its `=` initializer keeps the expanded bytes, and `+=` uses the caller's
integer attribute when computing the append. `local -I` and function-local
`declare -I` inherit the outer value and
declaration attributes. A nameref contributes its target spelling as scalar
data unless the new declaration gives the local its own nameref attribute.

The public script runner, `source`, `eval` and reached EXIT actions execute complete command units as
they are parsed. Earlier units retain their effects before later syntax failure.
A malformed arithmetic `for` header in a direct double-quoted command substitution
can also leave a complete prefix within the current unit. The shell emits the
diagnostic, publishes status 2, then executes that prefix and returns its status.
Explicit whole-script parsing remains available for callers that require validation
before execution.
Recoverable expansion failures discard the rest of their unit and expose the
failure status to the next unit. Readonly assignment failures and malformed
generated words stop execution when errexit applies at the script boundary.
The root policy also applies to failing commands in tested positions within a unit.
A tested `eval` or `source` invocation can recover within its nested units.
An invalid variable name in a complete for-in loop reports status 1 when the
loop is reached, before expanding its items. Prepared scripts use the same
execution rule. Missing loop syntax still fails during parsing.

`eval`, `source` and `.` consume one leading `--` from their expanded fields.
Other multi-byte dash options report status 2. A leading `--help` prints the
builtin's usage and returns 2. Later fields are ordinary operands.
`eval` joins its remaining fields with spaces and succeeds on empty input.
`source` and `.` require a filename and use any following fields as positional
parameters for that file. The filename must be valid UTF-8 for the host path
boundary. Script content and positional parameters retain their byte values.

An invalid braced parameter or empty subscript reports status 1 and ends the
reached command unit. It publishes `PIPESTATUS=(1)` in the active variable scope.
With errexit disabled, the reader continues at the next unit. A closed,
double-quoted word with an incomplete parameter subscript fails before its
substitutions run, while effects of earlier arguments survive. Source with a
missing closing brace fails parsing through the ordinary delimiter diagnostic.

A brace inside an array selector can belong to the selector when the same word
contains its closing bracket. A complete expansion then reads the selector with
that brace included. An unquoted reference with a complete selector can reach
the selector read before failing for a missing enclosing brace. Indexed effects
and selector diagnostics follow the indexed arithmetic recovery rules. A
parameter operator still requires an enclosing brace before its operand runs.

When arithmetic evaluation of an integer binding's assignment fails, the
current command unit ends and reached functions, `eval`, and sourced files
unwind. The script reader resumes at the next unit even under errexit. If the
prior status was zero it publishes status 1 and sets `PIPESTATUS` to `(1)`.
A prior nonzero status and its pipeline statuses remain visible. A command
capture ends with status 1 and retains earlier captured output. An EXIT
action ends at the failure and preserves the status that entered the action.

If numeric evaluation of an indexed array subscript fails after the subscript's
word expansions, the shell publishes status 1 and `PIPESTATUS=(1)` in the scope
where the index was evaluated, then unwinds the current command unit. The root
reader resumes at the next unit, including under errexit. A compound indexed
assignment retains the entries already applied to the array. Arithmetic failures
during word expansion, including expansion inside a subscript, publish status and
`PIPESTATUS` in the reached scope and use the enclosing word expansion's recovery
boundary.

When an arithmetic word takes its expansion pass, its array selectors expand
once. The indexed selector's brackets, apostrophes, and tildes remain escaped
for its numeric evaluation. An expansion elsewhere in the same arithmetic word
can select this path. Associative array keys retain their literal bytes.

`LINENO` follows the active parsed command. Functions defined in a script retain
their defining source coordinates. Sourced files and EXIT action source start
at line 1, and `eval` bases its coordinates on the calling command. Parenthesized
captures use the rendered body layout, while backtick captures use the decoded
body. Local and temporary bindings can shadow `LINENO`. Unsetting the dynamic
binding allows later assignments to create an ordinary variable.

An unhandled errexit status runs EXIT with the reached positional parameters and
loop or return target. An ordinary failure restores function locals and temporary
prefixes before the action. Subshell and pipeline child execution can keep those
bindings until it leaves the child scope. A simple failure restores the nearest
active simple invocation's redirections before EXIT. Compound command failures can
leave the reached redirection active. Timed failures report their timing before
the action. For a failing top-level brace group with a redirection, incremental
execution follows the pinned Bash script reader. Bash command-string invocation
can restore that mapping before EXIT.

EXIT actions preserve the triggering status on ordinary completion and parse failure
without errexit. Explicit return/exit and errexit failures can replace that status.
Arithmetic/default expansion failures end their unit; assignment-target and parameter
failures stop an errexit-enabled action. Pending loop flow skips later execution while
still validating remaining units. Caller tested-status suppression does not apply to EXIT.

Arithmetic uses wrapping signed 64-bit values, with decimal, octal, hexadecimal and
explicit base-2-through-64 constants. `0x` and `0X` alone represent zero. An explicit
base has no leading zero. Stored variable/array values are arithmetic
expressions evaluated against current bindings; they are not re-expanded as shell
source. Active numeric recursion shares the structural nesting guard and observes
cancellation. Completed arithmetic work consumes no execution allowance.
Arithmetic parser diagnostics preserve the failing operand and token bytes,
including significant trailing whitespace. Arithmetic commands, `let`, numeric
conditionals and substring bounds identify the reached caller in the diagnostic.

`let` reports `expression expected` and returns status 1 when expansion supplies
no expressions. A quoted empty expression evaluates to zero and returns
status 1 without a diagnostic.

Associative arrays enumerate keys in ascending lexicographic order of unsigned
bytes. Key and value aggregates follow that order, and value slices select
entries in the same order. GNU Bash can enumerate the same keys in a different
order.

Prefix and suffix removal apply the expanded pattern to the array's current
entries. The pattern can change or add entries before matching. Scalar values
are selected before the pattern expands. Empty scalars and aggregates with
zero fields skip pattern expansion. An aggregate with a supplied empty field
still expands the pattern.

Parameter transformations include `@a` for binding attributes and `@A` for
reusable assignments or declarations. Array `@K` produces a quoted key/value
string, and `@k` supplies keys and values as fields. A quoted `[@]@k` preserves
empty value fields. Quoted `[*]@k` joins pairs using the first character of `IFS`.
Associative transformations use the same byte key order as other aggregates and
declarations. Reusable quoting preserves bytes and renders invalid UTF-8 with
octal escapes.

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

Usage failures from `printf`, `mapfile`, `readarray`, `read` and
`getopts` retain status 2 when stderr is explicitly closed by shell
redirection. The shell discards those diagnostics before reserving a record.
Closing stderr also suppresses `getopts` option warnings while the scan still
updates its option variables.
Write failures reported by the host still follow the builtin's status policy.

Printf width and precision have no independent 1 MiB quota. A large precision that
only truncates a short string or leaves a short general-format result does not
require proportionally large workspace. Actual output, replacement overlap and
numeric material must fit the supplied live-memory allowance and CLR representation
limits. The deterministic Linux profile includes the alternate-form decimal point
for hexadecimal zero and discarded fractional zeros when a general-format carry
changes fixed notation to scientific notation.

For dynamic width and precision, Bash's field-range warning can name the following
operand. Range alone preserves successful status. An invalid numeric suffix
still retains its consumed prefix and returns status 1. For literal width
overflow or positive literal precision overflow, the Linux profile consumes the
argument for ordinary character, string and numeric conversions, emits no bytes for that conversion,
and subtracts one from the current format cycle's `%n` count. With `-v`, it
also removes the last buffered byte when one exists. Bash-specific conversions
use their own precision rules. In the Linux profile, a literal minus after the
precision dot produces a canonicalized format spelling for ordinary
conversions. The operand is consumed and numeric operand diagnostics still
apply. For Bash-specific text conversions that spelling selects zero
precision. Their literal width and positive literal precision accumulate with
signed 64-bit wrapping. `%Q` uses a signed 32-bit precision accumulator before
quoting. The minimum dynamic width leaves empty formatted values unpadded.
Padding for nonempty values is subject to the ordinary output representation
and budget limits.

Printf time-format literals preserve bytes, including malformed UTF-8. Directives
and widths use ASCII grammar with invariant date text; unknown directives follow
the pinned Linux strftime spelling. Explicit seconds use the configured fixed
offset with a Gregorian calendar. The calendar admits local years whose difference
from 1900 fits in a signed 32-bit field. Within that range `%s` retains the supplied
seconds. Numeric year rendering and padding follow the pinned Linux profile,
including its signed 32-bit year display. An out-of-range operand falls back to
the Unix epoch at the configured offset. The missing, `-1` and `-2` operands
select the configured timestamp. Each `%(...)T` conversion uses GNU's 128-byte
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
