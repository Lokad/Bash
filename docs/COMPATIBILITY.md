# Compatibility

The runtime supports deterministic, non-interactive shell workflows:

- Lists, `&&`, `||`, groups, subshells, functions, loops, conditionals and pipelines.
- Scalar, sparse indexed and associative arrays, namerefs and positional parameters.
- Quoting, ANSI-C quoting, brace/tilde/parameter/arithmetic/command expansion,
  word splitting, pathname expansion and quote removal.
- Redirections, here-documents, here-strings, logical descriptor aliases and
  allocated descriptors. `exec` can preserve descriptors or replace evaluation.
  An adjacent unquoted decimal in the nonnegative Int32 range selects the
  descriptor. A larger number remains an ordinary word. Simple commands use
  the operator's default descriptor in that case.
  Adjacent unquoted `{name}` supports file open modes, duplication/moves and
  here-inputs; variable closes follow ordinary command restoration. External
  command allocations and selector effects do not change caller bindings.
  A subshell expands its trailing redirections in its own scope, including
  binding changes made by the operands and allocated descriptor variables.
  If expansion leaves no command name, leading assignments still apply in
  the caller. A named descriptor redirection or a `<`, `<>`, close, or
  word-based descriptor duplication on stdin isolates the whole list.
  Its bindings are visible to later redirections in that list. The list
  reports its own status, and the caller's descriptor mappings are restored.
- Control, directory, variable, input and formatting builtins, including `read`,
  `mapfile`, `printf -v`, `source`, `eval`, `command`, `type`, `getopts` and `help`.
- `time`, host-mediated filesystem predicates, per-shell trap registration and EXIT
  handlers. Cancellation and external signals do not deliver traps.

`trap` accepts repeated and grouped `-p` options before its first operand.
A reached `--help` prints help and returns 2. With closed stdout it returns 2 silently.
Invalid options report the first invalid flag and usage on stderr. A shell-closed
stderr suppresses the message and preserves status 2.
Invalid options, invalid conditions and listing write failures include the
reached line and any active source filename. A lone unrecognized operand
reports usage.
Trap listings report a known output failure through the active stderr.
With shell-closed stdout, named listings check the remaining signal names
before reporting the write failure. Downstream reader closure stops output
and keeps the status from any earlier invalid name.
When conditions follow, an all-digit first operand from 0 through 64 selects
the historical reset form. Larger decimal operands remain literal actions.
The supported conditions are `EXIT`, `HUP`, `INT` and `TERM`.

`mapfile` and `readarray` split standard input on LF by default. `-u fd`
selects an existing logical descriptor after redirections are applied. Each
explicit selector must name an open descriptor, and the last one supplies the
input. Closed default input and known write-only handles supply empty input
without a host read. With empty input the reader clears the array, preserves
its entries when `-O` is present, and invokes no callbacks. `-d delim`
uses the first byte of `delim`, with an empty operand selecting NUL. `-t`
removes a record's final delimiter when present. Array values end at the first
NUL byte, including when NUL is the delimiter.
`-s count` skips records using the selected delimiter before `-n count`
limits how many records are retained. Zero skip leaves the input position
as is, and zero retained count reads to EOF. A reached limit leaves the next
record on the descriptor. EOF during skipping supplies empty input.
`-O origin` overwrites consecutive entries starting at the supplied index and
preserves the other entries. These three operands accept unsigned 32-bit
decimal values. Destination indices continue from zero after index 4294967295.
`-C callback` evaluates shell text on every quantum-th retained record,
after trimming the delimiter requested by `-t`. It appends the index and
record as arguments. The callback sees the earlier assignments before the
current record is stored and can consume input from the same descriptor.
If a callback issues loop control, the reader stores the current record and
finishes the requested input without invoking more callbacks. The requested
`break` or `continue` takes effect after the reader settles.
With effective errexit, a reached untested loop-control failure in a callback
exits before storing the current record and skips EXIT.
The index is spelled as signed decimal, while the array position uses
the unsigned index. `-c quantum` accepts positive unsigned 32-bit decimal
values and defaults to 5,000. Skipped records are excluded from the interval.
An invalid quantum fails before input is consumed. The callback evaluates
with the current errexit option, including when the array reader is tested.

`read -a array` replaces an indexed array with all fields from one logical
record using IFS and the ordinary delimiter and escape rules. It checks the
syntax of the first positional name before consuming input.
The array destination is checked after reading, and positional names receive
no assignments. A record with no fields assigns an empty array. At EOF it
publishes fields from the partial record, or an empty array, and returns failure.

`read` with no positional or array destination assigns the whole normalized
record to `REPLY`, preserving leading and trailing whitespace.
`read` skips unquoted NUL bytes unless NUL is the chosen delimiter. With
escape processing enabled, a quoted NUL ends the text available for assignment
and counts as a protected empty field when it occurs on its own. The read still
consumes the logical record, including escaped delimiters. If Bash 5.2 emits
its internal `0x01` marker for that empty field, Lokad.Bash assigns empty text.
Literal `0x01` input remains data. During field splitting, a backslash protects
one byte. UTF-8 continuation bytes after a protected lead byte use malformed-byte
IFS membership. With `-r`, backslashes remain literal.

`read -n count` stops at the byte count or an unescaped delimiter. If the count
is reached first, the next read starts at the following byte, including any
delimiter there. `-N count` ignores the delimiter and disables IFS splitting.
The last count applies, and any reached `-N` selects the exact-count mode.
Counts use the C locale byte profile, including for UTF-8 input. Escape
processing skips backslash-LF and counts the protected byte for other escapes.
Unquoted NUL does not count toward either limit. Zero assigns an empty value
without consuming bytes. A closed standard input or a known write-only
descriptor returns failure after assigning empty input. The shell keeps
the access mode of its file opens and pipe endpoints through descriptor
aliases and child evaluations. An opaque inherited host handle whose mode
is unknown uses the open-descriptor result. An explicit `-u` for a closed
descriptor fails before assignment. EOF before the requested boundary
publishes partial input and returns failure.

`read` accepts `-e`, `-i text`, `-p prompt` and `-s` in its non-interactive
input profile. The option scanner consumes the text and prompt and resumes at
the next field. Reads use the same descriptor and assignment rules, with no
line editing, prompt output or terminal echo changes.

`$(<file)` and its backtick form read a single input-file redirection on
descriptor zero. If the capture begins with input-redirection syntax and its
final command unit consists of one such redirection, it expands and reads the
final target in the caller. Otherwise a reached terminal input redirection
in its own unit writes file contents after the preceding commands in the
capture's child context. A same-line list stays an ordinary command unit.
Backticks retain trailing input that can prevent the terminal read. The result
removes NUL bytes with a diagnostic and strips trailing LF bytes. CR is
preserved. Input failures reported by the host retain their failure status.

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
assignment-shaped arguments such as `printf '%s' x=~:~`. Arguments generated by
brace expansion use the leading-prefix rule, so `x=~:{a,b}` produces `x=~:a`
and `x=~:b`. Ordinary words and implicit
indexed compound values use only the leading prefix. For example, the ordinary
word `~:~` expands to the HOME value followed by literal `:~`. In an ordinary
word, a quote or escape before the first slash prevents the tilde expansion.
The unquoted colon tail uses the rendered word spelling, including any
substitution before the slash. Expansion resumes at the slash even when it
falls inside that spelling. Associative compound elements retain their
existing literal tilde behavior. Quoted, escaped, or expanded colons and
here-document text do not start tilde prefixes.
An assigning parameter default (`${name=word}` or `${name:=word}`) expands a
leading unquoted tilde and continues expanding its suffix. Subsequent tilde
prefixes after colons in its own operand stay literal. Nested unquoted default
and alternate words use the assignment tilde rules.

Function-call prefixes such as `x=prefix f` supply the initial value of a first
local declaration, including through unprefixed helper calls. Unsetting that
temporary binding reveals the saved caller; subsequent assignments can survive
the call. Unsetting a local declaration instead keeps its unassigned local
namespace. A prefix on `local`/`declare` itself has a separate lifetime.
At script scope, a prefix on `declare -g` or `typeset -g` leaves the saved
value and export attribute available to the declaration. Appends use that
value, and explicit `-x` or `+x` changes its export attribute. Named `-p`
inspection still sees the active prefix.
If a temporary scalar is converted to an array, a helper's new local inherits the
structured array value. This deliberately preserves its data and kind where both
pinned GNU 5.2 references emit a scalar containing byte 01 in that nested case.

Variable-prefix expansions `${!prefix@}` and `${!prefix*}` include assigned
names from enclosing function scopes. An unassigned local can hide a value
while the outer assigned name still appears in the listing. Matching names
appear once in unsigned-byte order. Ordinary parameter reads follow the
active binding, and quoted `*` uses the ordinary IFS join rules.

Ordinary nameref initialization rejects an empty array selector. A selector
containing a space is accepted and remains unevaluated until reference use.
A malformed nonempty replacement fails before creating a local binding.
A failed empty or appended initializer can leave an ordinary local binding
with the attributes established by the declaration.
Self-referencing nameref declaration initializers fail at script scope without
replacing the binding. Function declarations warn instead; circular local reads
and ordinary writes can select the global binding. A self-element target keeps
its separate selector semantics. Assigning an unset nameref may create a cycle,
and raw declaration inspection does not resolve that cycle.
Circular nameref warnings report the reached command line and the current
reader's filename, when present. Compound declarations report their lookup
warnings after initializer expansion. A circular `readonly -n` assignment
reports the lookup and assignment warnings and skips the attribute lookup.
A bare assignment through a cycle with no fallback discards the
current command unit. A parameter default through that cycle reports the
read and write warnings and returns status 2 at the root, or 1 from `eval`
or `source`. A default read through a local cycle can see the raw global
nameref spelling and therefore skip its operand. `read`, `printf -v`, and
`getopts` report rejected writes as command failures. A rejected `printf %n`
write reports its warnings and leaves formatting available.
Indexed input through a local cycle writes to the global binding for the
authored name. `read -a` and `mapfile` preserve the local references. When a
global nameref becomes an indexed array under `mapfile -O`, its target
spelling occupies element zero unless the input writes that element.
An ordinary local redeclaration through an existing local nameref can create
a local for its whole-name referent. A readonly reference can select a writable
referent. A readonly local in an enclosing function can be shadowed, and `-I`
copies its readonly attribute into the new local. A declaration without an
initializer can apply attributes to an existing readonly local. A conflicting
array kind in such a declaration reports a conversion error. A compound
initializer expands its items before reporting the local array kind conflict
and can continue to later declaration operands. An array initializer or array
declaration with `+r` that reaches a readonly scalar local declares its array
kind before reporting the readonly failure. An unassigned scalar becomes an
uninitialized array. An assigned scalar becomes an initialized empty array.
The readonly attribute remains set and a deferred selector remains unevaluated.
For writable local element declarations, integer, case and export changes
apply to the selected declaration binding before a deferred selector runs.
A rejected selector leaves those attributes on that binding. Expansions
that already ran while constructing the command's fields see the earlier
attributes.
When the local nameref already points to an array element, the complete
target spelling can name a separate local array. This includes stored `[@]`
and `[*]` selectors when the function owns the nameref. Readonly on the alias or
on the underlying array leaves that declaration available. An empty
selector with `-n` selects that local before rejecting the nameref flag.
A nonempty replacement initializer in that form is validated as a nameref
target before the array can be prepared. An append follows selector handling.
A nonempty selector with `-n` fails before that selection or selector evaluation.
A parsed compound declaration of a new local can shadow an inherited
element nameref. Redeclaring an element nameref already owned by the
function uses the complete selector spelling as the local array name.
Later compound initializers of that name use its own array kind. The
stored selector remains unevaluated.
When an empty element declaration with `-n` instead reaches an existing local
reference to a whole variable, the rejected declaration leaves an empty array
with the nameref attribute and no target. The array is initialized when the
old reference had a target. Named `+n` clears the attribute and makes the array
available for assignment. `unset -n` clears the local value. An ordinary
whole-name assignment before clearing the attribute leaves the array empty.
An ordinary element initializer with an empty selector through an unset
reference can leave the same array state. With `+n` on an empty selector,
the array keeps the nameref flag when the write is rejected. A reference in
the current frame whose target is missing can declare a separate local for
that target.
A local element declaration through an inherited cycle reports the lookup
warnings and can create a separate local. In the cycle's owning frame, it
prepares an empty array with the nameref attribute. A name-only declaration
stops there. A valid indexed initializer evaluates its selector once and
rejects the write using the indexed array's diagnostic spelling. An explicit
global element initializer can write to the global declaration behind the
active cycle while leaving the local references in place.
A braced whole-value read with an invalid array nameref spelling
discards the current command unit, including when an operator tests
or inspects the value. An unbraced read expands to empty when the
array is uninitialized. A nonempty invalid spelling on an
initialized array discards the unbraced command unit. Element reads
through an invalid projection still evaluate their arithmetic
selector. Length reads and element removal skip the selector in
that case. A direct element write diagnoses the invalid target after
the RHS and a valid selector have run. A parameter default write
selects the element again after evaluating its operand.
Indexed array namerefs use the pinned Bash byte spelling for the diagnostic
subject and for indirect expansion. Associative diagnostics use the binding
name, and associative indirection starts with an empty spelling.
Initializer validation can reject the value after the array kind is prepared
and before the requested attributes are applied.
When this preparation reaches a nonempty selector, its arithmetic effects
occur once, including when the prepared array is associative. A valid index
still leaves the prepared array empty. The invalid-identifier diagnostic
uses the declaration operand's spelling.

For a whole-name `local +n` declaration, a target spelling that has no
binding becomes a local of its own. A readonly alias can take this path and
keeps its attributes. An element spelling such as `array[1]` can name that
separate local while the array keeps its existing elements. When `+n` clears
a local reference, it validates the initializer as a reference spelling
before storing the resulting scalar. A name-only `+n` can clear an unset
readonly reference and preserve the readonly attribute on its name.

With named operands, `readonly -n` processes assignments and preserves
existing attributes. It skips adding the readonly attribute. Without operands,
it lists readonly bindings.

An expanded declaration operand such as `declare -a 'items=(one "two words")'`
can initialize an array. Explicit array options select this form for declaration
builtins. `declare`, `typeset`, and `local` also recognize it when the selected
binding is already an array. The initializer uses the compound array grammar and
expands its items after the other arguments, with the declaration's attributes
and redirections in effect. A malformed list discards the current command unit.
With errexit set it exits with status 2, except in a source or eval reader that
was itself invoked as a tested command.

In an assignment-only command, a compound list on an array element, including
an empty list, reports an assignment error before expanding either the selector
or the list. Without errexit, a later command unit can proceed. Immediate
trailing text can form a scalar element value, as in `items[2]=()tail`.

A plain assignment can group spaces and line feeds inside an array subscript.
A declaration operand reads an array subscript within its lexical word.
Bare spaces and tabs separate its operands, and an unescaped, unquoted line
feed can end the command. Quoting, line continuations and nested expansions
can keep the subscript in one word. Compound initializer items use their own
bracket grouping.

A negative index in an indexed compound initializer counts backward from the
highest index already present as the initializer is applied. An out-of-range
negative index reports the expanded item, including its operator and value.
An ordinary initializer skips that item and applies later items. With errexit
active, it exits before applying later items and keeps the earlier writes
visible to EXIT. The exit status is the current nonzero status or 1.
A tested `source` or `eval` reader retains its errexit suppression.

An empty indexed initializer selector follows the same failure rules.
An `@` or `*` selector reports `cannot assign to non-numeric index`
and sets both `$?` and `PIPESTATUS` to 1 for later expansions.
Without errexit, an assignment-only command uses that status if it has
already run a command substitution. A later substitution can supply its
own status. With errexit active the invalid selector exits with 1.

An associative replacement builds its new entries while expansions read
the live binding. An explicit `[key]+=value` reads its append base from
that binding. Direct assignments and expanded declarations append each
entry before expanding the next key and value. An empty associative key
reports the reached item and skips its value expansion in those forms.
Parser-recognized declarations expand their operands before applying
items or diagnosing an empty key. An errexit failure abandons a staged
replacement and keeps earlier writes to an append target.

`declare`, `typeset`, `local`, `export`, and `readonly` report usage after an
unrecognized leading option. A reached `--help` writes the command's synopsis
and Lokad.Bash's builtin documentation to stdout with status 2. At script
scope, a leading `local --help` still prints help. Other `local` invocations
require function context before reporting option errors.

In function listings without named operands, a positive `-a` or `-A`
array filter selects no rows and returns success. This applies alongside
function attribute filters and `-p`.

With no operands, `local -F` and `local +F` list the current frame's
variables. Scalar values and nameref targets are omitted. Initialized
arrays include their compound values, including `()` for assigned empty
arrays. A named `local -F` operand still selects a function.

`declare`, `typeset` and `local` support `-l`, `-u` and `-c` case
attributes. They affect later assignments, including compound arrays,
appends, case-aware parameter-default results, and values read by `read`
and `mapfile`. Adding an attribute to an existing binding leaves its current
value in place until an assignment. Array selectors continue to use the
original key spelling. Integer attributes take precedence when evaluating an
assigned value. Case conversion uses the invariant UTF-8 character profile
and copies malformed bytes verbatim.

Replacement prefixes for a command use the supplied value bytes. The first
append can apply the caller binding's case attribute. The temporary value drops
that attribute for later prefixes and assignments within the command. A saved
binding keeps its attributes through restoration and persistent publication.

In parameter case conversion, an omitted pattern or a pattern expansion
that produces no fields selects every character the operator considers.
An explicitly quoted empty pattern or an empty aggregate field supplied
as the pattern matches no characters.

A double-quoted word keeps an empty field from case conversion, replacement,
pattern removal, escape transformation, or an in-range slice of an assigned scalar, even
beside a fieldless `@` expansion. A plain unset parameter or an out-of-range
slice contributes implicit empty text. Direct quoted `*` joins preserve a
field when they join one or more supplied empty fields to an empty result.
Transforms and slices of actual `*` aggregates keep their own join rules.
In double quotes, a selected default or alternate with a fieldless `@`
operand contributes an empty field. A rejected `:+` on a set `*`
aggregate with an empty join also keeps an empty field. These empty fields
and those from direct `*` joins persist through nested operands.

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
`dirs` lists paths on one line by default. Separate `-p` and `-v` options
select one entry per line, with stack indices in verbose mode. The last
signed selector chooses one entry. `+N` counts from the top and `-N` from
the bottom, starting at zero. Listing output abbreviates a matching HOME
path component prefix with `~`, including in `pushd` and `popd` listings.
`dirs -l` uses the original spelling. `dirs -c` validates its operands
and leaves only the current directory on the stack without writing output.
`pushd` and `popd` accept signed stack indices and `-n` for edits that
retain the working directory and its variables. They finish option validation
before navigation. Ordinary `pushd` accepts one path, and `popd` rejects stray
operands before `--`. The terminator ends option scanning.
`pushd` reports the first out-of-range selector. An empty `popd` field ends
option scanning and uses the preceding selector's count to choose a saved
entry from the bottom. With no preceding selector it removes the bottom entry.
A leading `--help` for `dirs`, `pushd` or `popd` returns 2 and leaves the stack
intact, including when stdout is closed. Usage errors retain status 2 when
stderr is closed.
A navigation operand of `-` in `pushd` selects and announces OLDPWD.
If that announcement fails, the changed directory and its bookkeeping survive
and the saved stack keeps its existing entries. An empty path revalidates the
current directory. A directory stored by `pushd -n dir` retains its supplied bytes
until a later navigation resolves the path through the host.
`DIRSTACK` exposes the current directory and saved entries as an indexed array.
Index zero follows the active directory independently of `PWD`. Saved entries
keep their byte spelling, including relative paths. Writing an existing saved
position changes the path for its next navigation. The active and out-of-range
positions ignore writes. Unsetting an element leaves the stack intact.
Unsetting the whole variable removes the live binding. An inherited environment
value remains an ordinary scalar in a fresh launch. Local and temporary
bindings can shadow the array, and `local -I` retains the live binding.
Children start with their own stack. A named declaration listing shows the
current array.
Successful stack edits retain success if the
listing write fails, and the write cause is reported through active stderr.
`pwd`, `dirs`, and declaration and option listings report write failures.
With shell-closed stdout, operand-free `local` and operand-free `declare`
attribute and function listings report the write diagnostic and keep their
listing status. Assignment-style listings and named declaration queries
return failure on a closed descriptor. Named print inspections report missing
operands before the buffered write diagnostic. Named function listings without
the print flag report the write diagnostic for each reached function.
Host write errors still fail a declaration listing. Downstream reader closure
stops the listing and preserves any failure already reported for an operand.

For an operand-free variable listing, `declare` applies array-kind filters
and selects bindings with any of the requested positive attributes. A
positive case option remains a listing filter even when a later option
clears it for assignments. Named print operands select their bindings
directly. Attribute listings can display a name that has attributes and
remains unset. Assignment-style listings omit names without values.

`caller` uses a compact display of active function and
sourced invocations for unnamed script input. Both `source` and `.` appear
as `source`. An omitted index selects the current invocation. Explicit
indices select enclosing invocations starting at zero. A leading `--` ends
option scanning. Invalid options and malformed indices report usage and
return 2. The error includes the reached line and any active source filename.
A valid index beyond the available frames returns 1. A leading
`--help` requests help and returns 2 even with closed stdout. Frame output
returns its selection status even if the write fails.

`cd` accepts at most one expanded directory field after leading options and
an optional `--`. Word splitting and pathname expansion apply as for ordinary
arguments. With no field, `cd` uses HOME. A lone `-` uses OLDPWD and prints
the selected spelling once the destination is reached. Missing HOME or
OLDPWD is an error when selected. A spelling such as `./-` is an ordinary
path. An empty destination revalidates the current directory when reached
through the ordinary lookup.
Before a `..` component removes a directory supplied by the operand, that
directory must exist and be a directory. This also applies to CDPATH
candidates and deferred directory stack entries. A failed traversal reports
the original operand spelling. A parent component at root stays at root.

Explicit relative directory operands can use CDPATH. Absolute operands, a first
component of `.` or `..`, and the HOME and OLDPWD forms use ordinary lookup.
CDPATH components are tried in order with leading tilde expansion. An empty
component tries cwd silently. A successful nonempty component announces the
selected absolute directory. If all candidates fail, ordinary lookup supplies
the result and any diagnostic. The same search applies to relative operands
reached during stack navigation. The directory model uses one virtual absolute
path. `-L` explicitly selects the default logical navigation, including in an
option group. The last `-L` or `-P` selects the mode. Bash's `-e` flag checks
the physical working directory. Logical navigation treats it as a no-op.
A final physical selection returns usage status 2 because the host lookup
supplies directory information without the resolved directory spelling.

Failed `cd`, `pushd` and `popd` lookups report the selected destination's
spelling, including relative operands, values from OLDPWD and deferred
stack entries. A saved resolved entry uses its stored path.

Directory builtin errors and readonly bookkeeping errors report the reached
source line and the active `source` filename, if any. The first command
element supplies the line unless an initial command word is followed by a
token on a later line. Usage lines keep their builtin name prefix.

A leading `cd --help` prints help and returns 2 before any navigation.
An invalid leading option reports the first rejected flag byte and the
supported usage. It returns 2 even with shell-closed stderr.
`pwd` accepts leading `-L` and `-P` options, including bundles. Both use the
single current directory tracked by the host model. Invalid leading flags
report usage and return 2. A reached `--help` returns 2, including with
shell-closed stdout. `--`, a lone `-` and the first operand end option scanning.

A fresh evaluation starts with OLDPWD exported and unassigned. It appears in
declaration listings and enters the command environment after it receives a value.
Inherited startup preserves a captured OLDPWD value, including empty or opaque
bytes, under the same environment contract as other captured variables.

After successful navigation, directory bookkeeping writes OLDPWD before PWD,
preserving array kinds, namerefs and attributes. A direct command prefix on
either name retains its directory write. A prefixed scalar target reached
through a global nameref restores its saved value after the command.
A function-local nameref can publish the write to its selected caller binding.
For an element nameref, the active binding supplies the selector kind and
bounds. A matching prefix retains the selected write in the saved caller
binding when that binding exists. An indexed selection through a temporary
scalar can convert a saved associative array to indexed storage. A prefix
on the local element nameref itself leaves the nameref intact.
Readonly failures diagnose and return 1 after changing cwd. Eligible writes
still occur, while push/pop completion and stack output are suppressed.
With errexit enabled, a readonly bookkeeping
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
operands. A simple command or trailing redirection needs a semicolon or LF
before a brace group's closing delimiter. A compound command's closing token
can supply the boundary itself, as in `{ (printf value) }`.
Brace expansion can join alternatives across quoted or escaped fragments and
substitutions. Protected commas are data and alternatives retain their original
quoting through splitting and pathname expansion. A word containing an opening
brace can fail the delimiter check even when its braces are quoted or belong to
a parameter expansion. Within double quotes, this check can see arithmetic
syntax inside a legacy backtick capture. For declaration operands such
as `declare x+={a,b}`, the generated words undergo ordinary argument expansion.
The builtin then applies each resulting operand in order. A backslash or backtick generated
by a character range can change how subsequent quotes and substitutions are read.
An incomplete generated substitution fails when that branch reaches expansion.
Brace sequences accept signed 64-bit decimal endpoints or single ASCII letters.
The endpoints choose direction and a zero step selects unit magnitude. Leading
zeros on unprefixed or negative endpoints request padding to the wider endpoint,
including its sign. The pinned Bash 5.2 profile formats padded results as signed
32-bit values after computing the sequence in 64 bits. Empty or overflowing
increments remain literal.
Command substitution removes NUL bytes and trailing LF bytes while retaining status.
Removing NUL bytes emits a warning at the reached command's source location.
Case subject and pattern expansion use the line where the subject ends.
An adjacent subshell opener can begin a command substitution, as in
`$((printf value);)`. A complete authored `$((…))` shape selects arithmetic
even when its expression is invalid. When rereading a reached arithmetic
expansion, an unquoted `#` after a space, tab or LF covers the rest of that
physical line for delimiter matching. A missing closer fails before operand
expansion. Comment bytes remain in the arithmetic operand when the same
closer is visible. A shifted closer can select the adjacent subshell form.
The modern and bracketed arithmetic readers also validate nested expansion
delimiters inside operand quotes. Failure there leaves earlier word effects
visible and skips the arithmetic operand.
The command grammar handles the subshell
form and consumes case arm closers, comments and completed here-documents.
Both pinned Bash builds reject some adjacent forms containing a case arm
without its optional opening parenthesis or an unpaired parenthesis in a
comment or here-document body. The corresponding sampled forms with a space
before the subshell opener, as in `$( (commands);)`, agree with both pins.
If that grammar rejects a balanced adjacent form, the capture reads its source
in complete units when reached. A failing child unit reports its diagnostic and
leaves any earlier complete units' output and effects available. A skipped
capture stays inert.
A diagnosed command substitution grammar error in `source` or `eval` can
terminate the invoking shell even in a tested command. A token following the
body where the closer is required is reported with its reader line. A malformed
arithmetic `for` header in an unquoted substitution terminates with status 2. With an
enclosing double quote, the builtin returns the status of any complete command
prefix in that unit, or 2 if the unit has no complete prefix. A recovered
array delimiter error uses status 1 and ends a child shell running the builtin.
An unexpected token in a literal compound array initializer discards its command
unit. With effective errexit disabled in the current shell, the reader
publishes status 1 and resumes at the next physical line or after any
here-document body already collected from that line.
For a substitution grammar error such as a pipe with a missing command,
the parser publishes 257 in `PIPESTATUS` and shell completion uses its low byte, 1.
A command-substitution child unwinds the builtin before EXIT and uses status 1.
A direct pipeline builtin uses status 1 while EXIT still has the loaded reader's
bindings and descriptors. If an inner reader fails, the pipeline builtin
restores it before running EXIT in the outer reader. A function stage retains
its active reader and its parser status through EXIT. Ordinary syntax errors and incomplete substitutions
outside array initializers continue to use the builtin's return path.
An absent redirection operand reports the rejected token. An implicit final
newline counts as that token if the input reaches it before the operand. At
the end of a continuation in token spacing, an open capture instead reports
its missing closer. An earlier here-document can advance the reported reader
line while the displayed text still comes from the redirection header.
A final bare `time` or `!` in an affected parenthesized substitution reports
the unexpected closer and rendered source when reached, before executing any
of the substitution's commands.
The enclosing command unit ends with status 1 and earlier bindings retain their
values. The failure ends the script with status 2 under errexit. An invalid nested
here-document spelling in an ordinary rendered substitution ends the root script
with status 2. In `source` and `eval`, EXIT sees parser status 257 and completion
returns 1. With errexit enabled, both statuses are 2. A pipeline stage inherits
that reader status. A rejected capture during simple stage expansion runs the
inherited EXIT action through the stage descriptors, using the caller frame of
the enclosing reader. A shell function entered by a stage can register its own
EXIT action, which then sees that invocation's arguments.
A reached compound array initializer written in an assignment or declaration
checks the rendered item list for rejected capture syntax before expanding any
item. A rejected capture reports the line in that list and ends the root script
with status 2. Under `source` or `eval`, EXIT sees parser status 257 and
completion returns 1, or status 2 with errexit enabled. Compound prefix
items expand in order and the capture diagnostic includes the closing
parenthesis when it appears on the reported line.
If a quoted bracketed initializer item contains a rejected capture, the
command reader reports the item before executing the unit. It reports this
diagnostic even when the unit skips the assignment or only defines a function.
A declaration's assignment head can report before the complete item. An
earlier unit's active errexit makes the read fatal and omits the displayed
line. A tested `source` or `eval` retains the caller's errexit suppression.
An adjacent subshell opener retains its source separators, and a nested spelling
failure there ends the unit under ordinary execution. Function display and export
retain the original timing separator so reparsing a definition preserves the
point of validation.
Parenthesized capture rendering can omit a semicolon after a flushed
here-document. The resulting command list can combine operands or attach a
redirection to the preceding command. When a sequential connector drains a
pending body, the delimiter's line break separates the commands. After an
earlier command in the same chain, the flush leaves later semicolons intact.
The formatter retains the authored connector placement when rendering a
capture whose execution uses the replayed tree. If the rendered list fails to parse,
the reached substitution reports the unexpected token and rendered line
before any of its commands run. The rendered line retains enclosing word
punctuation and literal suffixes through the reached operand boundary. With
errexit the diagnostic omits the rendered line. Function listings show the
rendered body in word, parameter and arithmetic operands, and reloading a
listing reports its invalid syntax. Host function exports preserve the authored
capture. Inherited startup checks any rejected rendered capture before the
child's script executes. The diagnostic identifies the function and position
within the rendered import, and startup returns status 2. Backtick
captures and captures read directly from a here-document use their source
separators.
When a script ends inside a parenthesized capture, the script reader reports
the delimiter it was matching and the reached line, including for an anonymous
script. A pending here-document warning precedes the missing-closer diagnostic
and uses its own warning line. A conditional reader also reports the unread
token at its reached line. Earlier complete command units can already have
executed. Prepared parsing reports the syntax error before any evaluation.
If a pipeline operator or prefix still needs a command at a closing token,
the streamed reader shows that token and its physical source line.
When a pipe or list operator reaches EOF while waiting for its next command,
the streamed reader reports grammar EOF at the reached line.
A reached adjacent or backtick capture attributes token and grammar EOF
diagnostics to the command substitution reader.
An adjacent `(` at the start of a capture preserves the payload through its
closing parenthesis. Pending here-documents end at that boundary. Later lines
stay with the surrounding reader. Reaching the capture reports EOF for each
pending delimiter at the capture reader's source line.
For a capture without that adjacent opener, `)` with documents still pending
prompts the script reader to warn and collect their bodies from the next unread line. The remainder of
the header still parses, and body expansion waits for the capture to execute.
The warning belongs to the enclosing command unit, including short-circuited
commands. Reading a function definition collects the bodies once for later calls.
The warning prefix includes the source line reached when the reader emits it.
An EOF warning also reports the line where its document started. A subsequent
capture on the same header includes the lines already read for earlier captures.
An assignment selector retains those bodies when its source is used for
expansion or included in a function listing.
During unquoted here-document expansion, reading text after a capture closer
to collect an inner document can cause the match to fail. The diagnostic uses
the enclosing command's line. If the inner reader itself reaches EOF, the
diagnostic uses that reader's line. A pending inner document with only trailing
line feeds after the header can complete with an empty body. Both the read
and the subsequent execution report their warnings.
When the substitution reader rejects a token in expanded here-document data,
the diagnostic includes the reached token and the source line within that
capture. Earlier document text does not advance the capture reader's line.
The source line retains bytes after the token, including any CR.
A directly reached substitution syntax failure during here-document setup
discards the remaining commands in the current shell's command unit. The
root reader keeps a previous nonzero status and its pipeline statuses.
A fresh root can report status 1 before any foreground pipeline status
exists when the capture reader rejects the token or reaches the end of
its text while still looking for `)`. A missing quote or a failed attempt
to collect an inner document after the closer publishes status 1 to
`PIPESTATUS`. A source or eval reader resumes with status 1 in
the invoking shell. In a forked shell it publishes status 1 and exits.
A command substitution ends its capture with status 1 and keeps earlier
output. A subshell, pipeline stage or host command reports its failed
status to the enclosing list. With errexit active in the current reader,
EXIT runs with status 2 and the redirections already applied there. An
unexpected token inside a nested substitution can terminate the reader
and publish its grammar status even with errexit disabled.
When a rejected here-document discards a command unit, earlier redirections
remain active in the shell or enclosing redirect that owns them. An open,
alias or close after the rejected document never runs. A child can use its
own reached mappings in EXIT, and its parent retains the original bindings.
The diagnostic uses the reached command-unit boundary, including when
the capture's rendered layout supplies that boundary. A previously
defined function uses the opening line of its body. Arithmetic and
conditional commands put the end of their expression in the warning
prefix while retaining the document's own line inside the warning.
When an expanded here-document contains a malformed parameter, the
diagnostic includes the document bytes and the reached reader line.
A failed required parameter reports its expanded message at that same
line. These failures in compound redirections publish status 1 and a
single pipeline status of 1. With errexit active, EXIT sees those
statuses through the redirections already applied to the command.
An unquoted here-document reads the closing parenthesis of a `$((`
expansion before attempting adjacent-subshell syntax. A missing closer
reports the full spelling supplied to that expansion. Legacy `$[`
checks its closing bracket when expansion reaches it, including inside
a selected parameter operand. A missing arithmetic delimiter discards
the current command unit and publishes status 1. Isolated host
redirections return the failure to their caller.
Parameter transformations resolve the selected value before validating an
unknown operator. An absent value yields the ordinary empty expansion.
An assigned empty value reaches validation. A rejected transformation
reports the reached operand with status 1. Command substitutions in a
rejected operator can appear in the diagnostic without running.
When a streamed command list encounters an unexpected closer or separator, the
diagnostic shows the token and the physical source line. Missing conditional
operands identify the unary or binary grammar role and any open parentheses.
Pending here-document warnings from the reached header precede these diagnostics.
Sourced and evaluated commands include their reader line. Anonymous streamed
roots use the diagnostic body directly for reached tokens. The displayed line
retains its original bytes, including CR. An incomplete compound command reports
the reader line at unexpected end of input, including in an anonymous stream.
A final unquoted backslash in a script file continues the last line, even
without a physical LF. Command strings read by `bash -c`, `source` and `eval` keep the
final backslash as one literal byte.
Legacy `$[expression]` uses the same arithmetic evaluation as `$((expression))`,
with a single closing bracket outside nested brackets. Within a double-quoted
word, an unescaped inner double quote at that level causes a substitution failure
when the bracketed expansion is reached. Both forms preserve a quoted bracket in
an associative array key, including `a["]"]` in an unquoted arithmetic expansion.
Both pinned GNU builds report an arithmetic error for that unquoted key.
Here-strings expand one scalar operand without brace generation, field splitting
or pathname expansion, then append LF. In scalar assignments, assignment defaults
and here-strings, quoted array key expansions such as `"${!a[@]}"` and per-field
transformations of array or positional `@` fields join on the first IFS character.
Unset or empty IFS selects spaces. Inspection fields such as `[@]@k` and `[@]@a`
use the same separator in those contexts. Associative compound initializers use
spaces for the per-field transformations, matching the pinned Linux build.
In unquoted here-documents, backslash escapes
dollar, backtick, backslash and LF; double quotes remain ordinary data. Quoting the
delimiter disables body expansion.
Direct positional star expansions, indirect array key stars and aggregate slices
in the body join with spaces. Direct array value stars use the first IFS character.
An at expansion anywhere in the body enables the final IFS split of exposed
bytes, including separators in other expansions. Results are joined with spaces.
Quoted values and literal source keep their protected bytes. Parameter default
operands retain their here-document context, and assignment operands form their
stored value before the enclosing body is split.
For unquoted delimiters, escaped LF is removed while collecting the body, before
delimiter recognition. Continued lines can form a delimiter; a physical line that
only resembles one inside continued data does not end the body. The `<<-` mode
removes leading tabs from logical lines rather than from every physical fragment.
At script EOF, a pending here-document uses the collected body and warns before
its command unit executes. A final partial logical body line receives LF. A
continuation with no remaining line contributes no bytes. In `bash -c`, `eval`
and `source`, an unpaired backslash at the end of an unquoted body is followed
by byte `0xff` before the final LF, matching the Bash string reader. A Bash
script launched by filename treats that backslash and the final LF as a
continuation. A missing outer compound closer still reports its syntax error.
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

Fresh evaluations bind `IFS` to an unexported scalar containing space, tab and LF.
Unsetting `IFS` removes the binding and field splitting still uses those default
separators. Explicit inherited startup values can supply their own binding.
IFS lookup follows the current scalar value through namerefs, including mutations
of their targets. Cached membership follows that value's byte-slice identity.
This deliberately retains consistent target lookup where the two pinned GNU 5.2
builds give different stale-cache results for IFS nameref-target changes.

For an indexed `IFS`, current splitting reads the low-order bytes of the
greatest index in little-endian order up to the first zero byte. The empty
indexed array projects byte `FF` as a delimiter, matching the pinned Linux
empty-array controls. Quoted stars and assignment joins can retain a saved
first separator while arithmetic or an input builtin changes the current
indices. A direct scalar-to-indexed promotion by one of these writers keeps the
prior first separator and disables indexed splitting until the next refresh.
Direct assignments and the tested declaration and scope-settlement boundaries
refresh the saved separator. Scalar targets of namerefs always use their
current value. An unquoted star omits an inserted SOH or DEL join byte when
it is absent from the current indexed separators. Field data keeps its bytes.

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
Recovery from a token or missing capture-closing parenthesis in an expanded
here-document can create an empty indexed binding before `PIPESTATUS` exists.
The rejected bare-prefix recovery uses the same binding. Its entries stay empty
through later commands and assignments. `unset PIPESTATUS` resumes normal
status publication. A local or temporary command binding can shadow the empty
array within its own scope.
After an expanded here-document rejects command substitution syntax, ordinary
builtin lookup uses caller bindings in place of that invocation's temporary
prefixes. `export`, `readonly`, and `unset` still take their explicit temporary
binding paths. A function, `eval`, or `source` receives its prefixes as active
bindings. In those calls a temporary `IFS` can be read by parameter expansion
while field splitting and joining still use the caller's separators. A reached
operand expansion in a redirection in that shell restores ordinary lookup, and
assigning `IFS` refreshes the field separators. A quoted or empty here-document,
a literal descriptor close or duplication, and an isolated child redirection keep
the caller's recovery state. A command substitution starts with ordinary
temporary-prefix lookup in its child.
A standalone `!` or `time` prefix completes an empty command. It publishes
status zero to `PIPESTATUS` and sets `$_` to an empty value. Negation still
applies to the command's exit status.
A rejected command substitution ending with a terminated bare prefix follows
command-unit recovery when errexit is disabled. The root retains a previous
nonzero status and its `PIPESTATUS` values. After a previous success it
publishes status one. A fresh root reports status one before any foreground
pipeline status exists. `eval` and `source` publish status one in their
reader, including before terminating a forked child. An unquoted
here-document expands its capture from the authored command units, so a
terminated bare prefix inside that capture can execute.
A direct commandless stage reports an isolated redirection failure to stderr
and publishes its assignment status. Ordinary output redirection failures and
failures in a compound stage contribute their own nonzero status.
For `|&`, the left stage applies an implicit `2>&1` after its explicit
redirections. Setup diagnostics use the stderr mapping reached so far.
Readonly descriptor allocation reports the selected binding first and the
written allocation variable second, using the same source location for both
errors. A logically closed stderr stays closed during allocation.
After an ambiguous redirection, the evaluator expands the operand again to name
it in the diagnostic. Direct command substitutions remain text in that pass.
Arithmetic and braced parameter operands keep their own expansion behavior. If
the pass produces zero or multiple fields, the diagnostic uses the operand's
spelling. A descriptor move closes the destination when the operand produces
zero or multiple fields. A single quoted empty field reports a bad descriptor.
An empty or closed descriptor reports the reached source filename and line.
A closed literal source uses its descriptor number. An expanded duplication
reports the word spelling for the default descriptor and the destination
number for a non-default descriptor. An expanded move reports the destination.
A failed move to an open destination during restorable setup first reports
the failed duplication. A failed allocation also reports the duplication.
When the allocation operand supplies no valid descriptor number, the
interpreter uses an empty label.
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
`-o errexit`/`+o errexit`. With no expanded operands, it lists visible variable
assignments followed by function definitions. Each group uses byte-name
order. Like bare `declare`, this listing hides the command's own temporary
prefixes and prints function bodies without their declaration attributes.
A bare `+` continues the option scan. A bare `-` ends
the scan, with following operands becoming positional parameters. `--` replaces
the parameters even when no operands follow. Errexit stops unhandled failures,
including failed pipelines under the fixed pipefail policy. Tested conditions, logical-list
positions and negation suppress it through nested functions/groups and source/eval.
Command substitution clears errexit by default; parenthesized subshells inherit it.
`$-` and `${-}` expose the supported mutable flags. `set -o`/`set +o` inspect
errexit only; other option changes return an error with a diagnostic.
Downstream closure stops inspection output and diagnostics routed to the
same handle. Option and positional processing continues.
With shell-closed stdout, an inspection reports a write failure only when
it has no following argument.
Option errors and inspection write failures include the reached line and any
active source filename. Functions use the source of their definition.
Leading short flags are validated before they can change shell state.
During validation, `--help` describes the supported options and returns
status 2, including with shell-closed stdout. It leaves shell options and
positional parameters in place. A lone `+` ends validation and is skipped
when applying options. An unknown short flag reports its sign and byte
followed by usage. It returns status 2 during validation or status 1
during application, preserving any changes already applied. After `-`,
`--`, or the first positional operand, `--help` is a positional argument.
`$_` holds the last expanded argument of a completed simple command. A command
without arguments records its name, and a command with only assignments or
redirections records an empty value. Word and redirection expansion can read the
previous value. Function calls publish their invocation argument on return.
If `export` or `readonly` rejects a final element assignment, it records the
element name as the last argument.
Subshell and pipeline commands keep their own updates. The binding preserves
bytes and follows ordinary variable assignment rules, including readonly
diagnostics and local bindings.

An assignment rejected by `export` or `readonly` reports status 4 as a direct
pipeline stage. The pipeline publishes that value in `PIPESTATUS`. The same
rejection during ordinary command execution reports status 1.
These builtins can assign through a readonly nameref to a writable target.
An ordinary assignment through an element nameref evaluates the selector
before checking the target's readonly attribute. Applying the attribute to
an element reports a separate diagnostic. A temporary command prefix over the
target supplies the visible selector bindings and bounds. The selected
element write reaches the array saved under that prefix.
A temporary scalar that hides the element reference remains local to
the `export` or `readonly` invocation. The original reference is restored.

For nonlocal `declare` and `typeset` assignments, a readonly nameref can
select a writable target. A readonly array already present at the target
rejects the assignment before selector expansion. A direct pipeline stage
reports status 4 for that refusal. Ordinary element declarations make the
array and its attributes visible to the selector. A newly applied `-r` lets
a successful selector finish and reports the rejected write with status 0.
An ordinary integer declaration establishes its destination and applies its attribute
changes before evaluating the initializer. For an element declaration the
array kind and attributes are visible during selector expansion. A failed
initializer can leave an unassigned scalar or an empty array with its new
attributes. Earlier writes reached by arithmetic are preserved. A new
readonly attribute rejects arithmetic writes to that binding during
initialization. A whole-name declaration may still write its own final value.
An ordinary assignment through a chain ending at an unset nameref leaves
the chain intact.
When an ordinary declaration gives an unset nameref its first target,
export and readonly apply to that reference. This assignment leaves an
element selector unevaluated.
When nonlocal `declare` or `typeset` promotes a temporary value through
a whole-name reference, the active prefix supplies its value and new
attributes. The saved referent retains its array kind and other elements.
At script scope, applying export or readonly to a temporary scalar over an
integer binding converts the value before the next declaration operand.
Publication evaluates the staged text again in the restored bindings.
An explicit integer attribute on the temporary assignment converts its
value to decimal during assignment. Publication then reads that decimal
value. Arithmetic writes reached during conversion follow the prefix
scope's ordinary expansion rules.
An operand's failed conversion stops later operands. Previously completed
integer promotions still publish. If publication reports an arithmetic
diagnostic without an expansion failure, it retains the staged bytes and
preserves the status selected by the operands. A selector expansion failure
ends publication of that value and leaves the converted operand value in place.
When the prefix hides an element reference, promotion waits until the
declaration's operands finish. It evaluates the restored reference's selector
once using the caller's bindings and earlier promotions. Multiple prefixes
publish in Bash's temporary table order. A bad subscript reports a diagnostic
at publication and leaves the status selected by the operands.
After successful selection of an integer element, a failed value conversion
without an expansion failure reports a diagnostic and publishes the staged
bytes. A selector expansion failure stops the element write.
At script scope a promoted temporary scalar can give a saved unset nameref
its first valid target while processing operands. The final temporary value
publishes through that reference after restoration, with the destination's
assignment attributes. A later assignment operand can change the temporary
value while the saved reference keeps its first target.
An invalid first target reports an invalid identifier during operand
processing. If the saved reference is still unset at publication, it
retains the final spelling. This includes an explicitly empty value,
which reads as unset and satisfies `[[ -R name ]]`. Storage inspection
prints that value as an empty string. Direct reads of a malformed
nonempty target report an invalid name reference. Indirect expansion
returns the malformed nonempty spelling.
For a nonlocal declaration through an element nameref, `+a` and `+A`
reject removal of a matching array kind before evaluating the selector.
With neither array kind explicitly set, `+a` clears a scalar before the
selector runs and creates an indexed array for an absent binding before
rejecting removal. `+A` allows a scalar to become indexed and preserves
its value at index zero. An explicit array kind or attribute declaration
prepares the selected binding without evaluating the selector.

When one declaration supplies both signs of an array kind, the set flag
participates in declaration preparation in either order. A matching array
rejects removal. An existing array of the other kind rejects conversion.
With both indexed signs, a nonlocal name-only declaration clears the value
of an existing assigned scalar and retains its attributes. A local declaration
can prepare an empty local array before reporting the removal error. A
rejected write leaves the prepared binding in its selected scope.

A bare nonlocal `declare` or `typeset` through an element nameref
evaluates the stored selector and declares that cell without a value.
The array remains hidden until a later assignment makes its retained
cells visible. A parsed compound initializer expands its items before
preparation and can then report the stored element spelling as an
invalid identifier. A source integer or case flag prepares the reached
array and its attribute without selecting the stored index or key.

Inside a function, `local -` saves the active mutable option state. The function
restores it on return. A second `local -` keeps the first snapshot, as in the
pinned Bash 5.2.37 build. `local -p -` inspects the current function's snapshot.
Inside a function, `local -g` applies a declaration to the global binding.
Named `local -p` still inspects the current function's local binding.
A parser-recognized compound initializer takes its scope, array kind and
value transforms from source options. The builtin then handles the expanded
options and the resulting name, without repeating initializer effects.
Preparation of a conflicting nonlocal compound initializer reports the active
command label. A function call or loaded reader can supply that label.
Arithmetic and conditional commands supply `((` and `[[`. A completed simple
command clears the label. An isolated child inherits the label and updates it
in its own state. A second diagnostic emitted during builtin dispatch uses
the declaration builtin's name and the reached stderr mapping.

An explicit array kind conflict through a whole-name nameref reports the
declaration operand. Through an element nameref, a name-only declaration or
compound preparation reports the reached array. An explicit nonlocal compound
declaration expands the initializer before checking the array kind. A kind
conflict rejects the declaration before it changes any attributes. A compatible
declaration can apply source integer and case attributes even when the
initializer then rejects a readonly variable or a subscripted operand.
At script scope this also applies through an element nameref. The stored
element spelling is reported as an invalid identifier and its selector stays
unevaluated.

Inside a function, a parsed `declare -g` compound operand through an element
nameref prepares the global binding and can write to the literal selector
spelling without evaluating the selector. An existing visible literal can
receive that write. A new literal belongs to the global scope. Preparation
applies source attributes to the reached global binding. Builtin dispatch
applies the expanded options to the binding it subsequently resolves.

`type -p` prints the host's reported path when the selected name resolves to an
external command. Builtins, keywords and functions resolve successfully in this
mode and produce no output. `type -f` skips functions during lookup and combines
with `-p` or `-t`. `type -P` sends valid nonempty names directly to the host's
command lookup, including names that are also functions, builtins or keywords.
The last `-p`, `-P` or `-t` selects path or kind output. Once `-P` is supplied,
host-only lookup applies regardless of the output mode or the placement of `-f`.
`type -a` lists each matching keyword, function and builtin in lookup order,
followed by the command resolution returned by the host for that name. The
host interface supplies one result per name. With `-p`, local matches count
toward the status and only the host path is printed. `-f` skips shell functions
and `-P` selects host-only lookup with or without `-a`.
Descriptive lookup misses and host lookup failures report the reached source
location. A `type` output failure includes that location and the descriptor's
reported cause. `command -v` and `command -V` retain their lookup status after
a failed write.
Invalid options for `type`, `command`, `builtin`, and `exec` add the reached
source location to the first diagnostic line. The usage line follows without a
location. Unknown `builtin` operands and a missing `exec -a` operand also report
the reached location. Argument expansion and redirection errors take precedence.
During option scanning, `--help` writes the builtin synopsis and returns 2.
It returns 2 silently with closed stdout. After `--` or the first name,
`--help` is an ordinary name.

`help` reports the reached line and any active source filename for an invalid
option, an unmatched topic, or a failed topic write.

`hash` uses an empty command cache. A bare invocation reports that the
table is empty. `-r` clears it and `-l` lists its reusable entries.
Both succeed silently, including when bundled or repeated. With named operands,
`-d` succeeds silently and `-t` reports each queried name as missing with
status 1. Both options require a name. When combined, `-t` selects the query
and `-d` supplies the missing-name diagnostic. Names supplied for cache
population return 1 silently. A leading `--` ends option scanning, and a reached
`--help` prints the supported usage and returns 2. Each query miss, option
error and empty-table write failure reports the reached source location.

`shift` uses expanded fields and accepts a leading `--`. Its count defaults to
one. Explicit counts accept signed 64-bit ASCII decimal with leading ASCII
whitespace and trailing space or tab. A count larger than the remaining
positional window returns 1 silently. Negative or invalid counts return 1 with
a diagnostic.

Implicit `for name; do ...; done` iterates a snapshot of quoted positional
parameters, preserving empty arguments and bytes. Explicit `in` lists retain
their usual expansion rules.

`break` and `continue` select loops in the active shell and function. A
parenthesized subshell or compound pipeline stage starts with zero active
loops. Outside a loop, either builtin diagnoses and returns 0. A level-one
`break` or `continue` from a while or until condition is handled by that loop
after its condition gate. Higher counts can select enclosing loops within
the same function. A nonpositive count diagnoses, abandons all active loops
and returns status 1. Its diagnostic reports the first operand, including
a leading `--`.
An invalid numeric count terminates the current shell. It sets the high
bit of the status current after operand expansion and publishes that value
to `PIPESTATUS` in the reached scope before unwinding. Function-local option
snapshots restore before the EXIT action runs.

After numeric validation, surplus operands to `exit`, `return`, `shift`,
`break` or `continue` diagnose and discard the current command unit. The
outer script reader advances to the next complete unit, including under
errexit, after unwinding function calls and loaded `eval` or `source` input.
It preserves a nonzero preceding status and its pipeline statuses. After
a preceding success it publishes status 1. A subshell or compound pipeline
stage terminates on discard with the preceding nonzero status or 1. A direct
builtin pipeline stage or command substitution reports 1. A discard inside
an EXIT action ends the action and preserves the pending exit status.

Control-builtin operand and scope diagnostics include the reached command's
line and source filename when available. A function keeps the source of its
definition for these diagnostics after the source call returns. The message
follows the active stderr mapping.

`exit` and `return` use the low byte of an explicit signed 64-bit decimal
status. An invalid numeric operand diagnoses and selects status 2.
Inside an EXIT action, bare `exit` uses the saved status that started the
action. `exit --` uses the status current inside the action.

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
In a conditional pattern operand, a non-space first IFS byte makes
unquoted direct `$@` and `${array[@]}` fields literal. Sliced and projected
`@` fields omit empty elements in that form. With empty IFS, unquoted
`${array[*]}` and its supported value transforms join their fields without
a separator and treat the field bytes as literal. Authored pattern
operators alongside the expansion stay active.
Token-start comments apply inside `[[` and end only at LF. Delimiter-looking bytes
inside balanced regex groups remain operand data, including unquoted spaces,
LF and `#`. Shell quoting and escapes govern parenthesis balancing even within
a regex bracket expression. At the top level of a regex word, unquoted `|` is
alternation data and `&&` ends the word and resumes conditional parsing. An
empty regex word before `&&` or a conditional `)` uses the .NET regex profile.
Definition display and export render implicit nonempty predicates with `-n`,
use spaces around groups and connectors, and render locale quotes as double
quotes in the invariant locale. Nested captures use the same conditional
spelling when their enclosing condition is rendered.
Braced parameter operands decode authored ANSI-C literals during tokenization.
Unquoted operands and pattern operators preserve the decoded literal's quoting.
Double-quoted defaults, alternates, assigning defaults and error operands use
the decoded bytes as parameter syntax. A decoded delimiter can change the
reached operand or yield a substitution diagnostic. In a directly expanded
word a decoded NUL in one of these operands ends the normalized word. If its
prefix still needs a closing brace, the word fails before its expansions run,
even when the operator would skip the operand. An already complete prefix
expands normally. Only selected operands evaluate their expansions.
Definition display uses the prepared spelling, including truncated operands
in `[[` and conditional captures nested in ordinary words and other parameters.
With brace expansion enabled, the argument word list checks damaged capture
spelling before evaluating any of its words. A command-only assignment list
checks each value when it reaches that value. An ordinary capture can
report a delimiter error while rereading the normalized word. Adjacent
parentheses can report a missing delimiter in the truncated enclosing word.
Conditional listings keep subsequent operators and operands after a truncated
capture word. When `eval` or `source` reads a damaged spelling, it reports
the delimiter at the loaded reader's line. If a decoded NUL leaves an
evaluator-produced function export with an unclosed delimiter, the same-host
child reports the rejected import, publishes the other validated functions,
and runs its script. The child uses Bash's function table order for these
imports. The hosted function payload and accepted bindings retain their
namespace order.
The import diagnostic uses the invocation name. After that recovery,
the child continues with its own reader identity. A same-host command string
uses the bytes of its `$0`, including an empty name. Functions defined in a
command string use `environment`. Named child scripts and sourced files use
their path, which remains the origin of any functions they define. Imported
functions use `environment` and count their compact definition's lines from zero.
With the C locale, an explicitly quoted empty pattern matches in Lokad.Bash
and the pinned Linux Bash build. The pinned MSYS build returns status 2.
LF is accepted before a term or after a complete predicate. It cannot split
an operator from its required operand or
follow the left word before predicate selection. Backslash/LF continuation retains
its ordinary lexical rules. Comments do not execute expansions or terminate early
on quotes, `]]` or CR.

Extended shell patterns `?(...)`, `*(...)`, `+(...)`, `@(...)` and `!(...)` are
unsupported in `[[`, case arms and parameter pattern operators. Case allows line
breaks between the subject and `in` and an optional opening `(` before each arm's
patterns. A reached `;;` ends the case. A reached `;&` executes the next arm
without expanding its patterns. A reached `;;&` resumes pattern matching against
the original expanded subject. The case returns the last selected arm's status,
with zero for an empty arm or when no arm is selected. Definition listings
leave the body line of an empty arm blank. Inside an explicitly
parenthesized arm, an unquoted `esac` can be
a literal pattern. Lokad.Bash applies that grammar inside command substitutions
and captures the selected arm's output and status. In the recorded conformance
case, both GNU Bash pins produce text from the arm's source inside the capture.

Quoted `$@` and `${array[@]}` in a case subject join fields with spaces. An
empty field contributes an empty payload, and a literal DEL byte (`0x7F`)
remains data. In the recorded multi-field comparisons, both GNU Bash pins
select the same DEL-containing pattern for an empty field and a field that
contains DEL.

In a case pattern, quoted `$@` and quoted array `[@]` can produce separate
fields. The matcher uses the first field, including a supplied empty field,
after the whole pattern has expanded. When expansion starts with empty IFS,
a direct aggregate keeps the separator chosen when it expands. A quoted `@`
aggregate contributes a DEL byte (`0x7F`) for an empty argument unless it is
the pattern's sole byte. When an earlier expansion sets a non-space IFS,
direct unquoted `$@` also keeps these bytes and joins using the first IFS
character. A direct unquoted named array keeps these bytes and joins with
spaces. A slice in this case keeps its join bytes and leaves unquoted
wildcards active. A literal DEL remains data.

With a nonempty IFS that starts with a character other than space, a case
pattern containing unquoted `@` splits at exposed IFS characters and rejoins
the fields with spaces. This includes adjacent unquoted text and `@`
expansions that supply zero fields. Direct `$@`, `${@}` and `${array[@]}`
protect their values from splitting and glob matching. Unquoted slices and
projected values expose their bytes to both operations. An empty field
created by a non-whitespace delimiter or retained from a quoted aggregate
can contribute DEL (`0x7F`). A terminal delimiter supplies no additional
field, and a lone synthesized DEL is removed. A selected parameter default
or alternate can join fields before the enclosing pattern splits them.
Quoted `$*` uses the first IFS character as its separator.

Case selection also follows Bash's quoting rules when IFS contains SOH
(`0x01`) or DEL (`0x7F`). Those bytes can participate in field selection
as delimiters or as part of the quoting representation. This applies to
quoted groups and selected parameter operands before the final match.
Each expansion uses its current IFS to record which quote bytes are
exposed, and the later selection uses the IFS in effect at that point.

In parameter replacement text, later empty fields from quoted `$@` or
`${array[@]}` emit a DEL byte (`0x7F`) in the recorded Bash profile. A leading
or single empty field contributes zero bytes. A literal DEL remains data.

Word and arithmetic `for` loops accept a brace body as well as `do ... done`.
A word loop needs a semicolon or line break before the opening brace. An
arithmetic loop can place the opening brace immediately after `))`.
An arithmetic `for` header requires exactly two unquoted semicolons outside
shell expansions. A malformed separator count is a syntax error before any
clause or loop body runs. The command-unit reader distinguishes a missing
separator from an extra separator and quotes the normalized header on the
next diagnostic line. Within an expanded here-document, a rejected capture header
reports the line in its capture and includes the expression when errexit is
disabled. A rejected nested capture header terminates the active reader.
A capture that runs out of closing parentheses during
separator scanning consumes the remaining header spelling for that scan.
An empty condition or one containing only spaces
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
When listing a definition, parsed `$(...)` bodies use Bash's command layout.
Newlines between commands and in literal data remain part of the spelling.
Trailing comments and blank lines are omitted, and a here-document keeps the
newlines needed to delimit its body. Pending here-documents from the left side
of `&&` and `||` print before the right command. The connector stays on the
preceding command line so the listing can be reloaded, including after a
pipeline's final stage. A sequence separator that drains a command chain's
final here-documents leaves later statement separators intact. This layout
can preserve execution and reload for forms that the pinned Bash builds
reject after rendering. A protected delimiter is displayed as a single-quoted
word, with escaped apostrophes where needed.
Reloading the listing with `eval` locates commands using the printed lines.
When a command substitution starts with an authored adjacent parenthesis,
its spelling keeps internal whitespace, comments and any final separator.
Lexical quote preparation and nested substitutions still apply. The final
separator can distinguish a capture from an arithmetic expansion.
Definition listings use Bash's spelling for default descriptors in file and
here-input redirections. Descriptor duplications distinguish literal numbers
from quoted or expanded target words.
Timing prefixes keep their authored side of a subshell's parentheses in a
listing. An authored inner timer keeps its own `-p` spelling. A timed and
negated command prints `time` or `time -p` before `!`. Timing for a sole
subshell still runs inside it, with its active descriptors and `TIMEFORMAT`.
An invalid `TIMEFORMAT` reports the timer's reached reader location on the
active stderr and preserves the timed command's status. A timer inside a
function uses the body entry line unless an enclosing `for` or `case`
selects its own line. A sourced or evaluated command string supplies its
own reader coordinates.

`getopts` scans explicit arguments or the current positional parameters one option
byte at a time, including bundled flags. A colon after an option byte requires an
argument. The attached remainder or the next argument is assigned to `OPTARG`,
including an empty argument. A leading colon selects silent errors. `OPTERR=0`
suppresses diagnostics while retaining the normal error results.
A recognized raw option consumes any required argument before interpreting bytes
`FD`, `FE` and `FF` as missing, unknown and end-of-options results respectively.
An invalid option to the `getopts` builtin reports the reached source location
on the error line. The usage line has no location.
`getopts` reads the signed decimal prefix of the stored `OPTIND` and
`OPTERR` values after skipping leading ASCII whitespace.
An explicit `OPTIND=1` restarts the scan. A visible `OPTIND` can stay unchanged
between bytes of a bundle. A call reaching `--` returns status 1 and advances
past it. A later call can scan an option at that new position. An ordinary
non-option returns status 1 and keeps its position. `OPTIND` starts with the
integer attribute.
Successful `unset` of `OPTIND` or one of its elements reselects the index
for the next scan. The rule also applies when `OPTIND` is a nameref and
the unset removes its target, or when `unset -n OPTIND` finds an ordinary
variable. A local nameref named `OPTIND` reselects
the index on entry and again when it leaves scope as a nameref.
A whole-name nameref declaration for `OPTIND` selects the current index.
Later assignments to the target can change the visible value while
scanning continues from the selected cursor.
An ordinary local `OPTIND` restores the cursor from function entry,
even when the declaration follows an earlier scan in the function.
The visible binding is restored from the declaration boundary.
When a function exits with a temporary `OPTIND` prefix still in scope,
it restores the option cursor selected at function entry. The visible
binding follows the ordinary prefix restoration and promotion rules.
A prefix that temporarily replaces the referent of an `OPTIND`
nameref also restores the function-entry cursor. The prefix value is
visible in the body while the scan uses the previously selected index.

`declare -i` and `local -i` evaluate subsequent assigned values as arithmetic
and store the decimal result. For integer bindings, `+=` adds the evaluated
values. `+i` clears the attribute. A command prefix drops the integer attribute.
Its `=` initializer keeps the expanded bytes, and `+=` uses the caller's
integer attribute when computing the append. `local -I` and function-local
`declare -I` inherit the outer value and
declaration attributes. A nameref contributes its target spelling as scalar
data unless the new declaration gives the local its own nameref attribute.
An element declaration without an initializer can prepare an array under
the authored local name when its nameref chain ends unset. The selector
stays unevaluated. Clearing an applicable attribute can report failure
after preparing the array.
With combined `-i` and `-n`, a name-only declaration can retain both
attributes. An explicit initializer can fail after creating an unassigned
integer local or applying attributes to an existing binding. A missing
script-scope binding remains absent. Reached arithmetic still evaluates,
including the previous value followed by the new operand for append.
An integer initializer while clearing a local nameref can fail with the
reference and integer attribute in place. Clearing a nonlocal unset
nameref with such an initializer removes its binding.
With `+n` and an explicit array flag, a missing local target can receive
the declaration while the alias remains a reference. When the alias is
converted, a parsed compound initializer can write the referent before
dispatch creates the empty array on the alias. A simple indexed initializer
can report an invalid identifier with successful status and
leave the prepared array empty. An authored element initializer can
evaluate its selector once before reporting failure and retaining the
prepared array's nameref attribute. A compound declaration through a
function-local chain ending at an unset nameref prepares the array under
the authored local name. The nameref attribute can remain on that array.
Listings display its cells. Reads follow a valid projected reference
and treat the marked cells as absent when the reference is invalid
or empty. When the authored local is
readonly, compound preparation still publishes its cells and evaluates
the reached selectors. The current reader then returns failure without
a diagnostic. A direct declaration stops the function body. An `eval`
or sourced reader returns to the caller with the prepared array visible.
An indexed array with the nameref attribute uses the maximum index's byte
projection as its reference spelling. An assignment through an empty
projected spelling leaves the stored cells intact.
An ordinary indexed or associative declaration that keeps the nameref
attribute prepares the array under the authored local name when the
function-local reference chain ends unset. A source reference with a
stored target produces an initialized empty array before any initializer
write. A named indexed initializer can report an invalid identifier
with status zero, while a valid named associative initializer can
populate key zero. With a missing target, explicit `-n` and an array
flag prepare the target before rejecting the nameref flag.
A parsed compound redeclaration of an existing local array nameref can
replace or append cells while retaining the attribute. With `+n` the
builtin clears the attribute after a successful compound write. With
`-n` it can reject the flag after the write. A same-kind compound
initializer on a readonly array nameref publishes its cells before the
current reader returns failure.
After compound preparation, a valid indexed projection can select a
local declaration. An ordinary or array nameref owned by the current
function can extend that selection. A caller's nameref stays visible,
including when it is unset or its target is readonly. A selected element
spelling declares a literal local. A selected readonly global reports
the target name after retaining the marked array's cells.
Direct compound assignment through an uninitialized array nameref
warns and clears its nameref attribute before checking readonly or
expanding the initializer. An initialized indexed array nameref uses
its maximum index's projected spelling to select a destination. An
empty projection creates a new indexed array under the authored name
in the global namespace. A projected element spelling selects an
existing literal binding such as `array[2]`. An invalid spelling or a
missing literal binding reports the projected bytes before operand
expansion. Initialized associative array namerefs report the reached
binding name because the pins disagree on the diagnostic subject bytes.
A local declaration can prepare a literal local for an element
projection, then write it and apply attributes there. A simple
initializer validates its spelling as a nameref target when the
reached array still supplies the declaration binding. A named
associative initializer writes key zero and can retain the nameref
attribute. An authored element selector can run once before the
indexed projection's diagnostic. With an empty indexed projection,
the marked local keeps its cells while the initializer writes a
fresh array in the global namespace.

A simple assignment or arithmetic write through a valid indexed array
nameref projection selects the reached binding. A whole-name write uses
the binding's scalar slot or array key zero. An authored selector uses
the reached array kind and, for an indexed array, its negative-index
bound. A projected element spelling can name a literal local. Without
that local, a whole-name write selects the base element and an authored
selector diagnoses the projected spelling after its effects. An arithmetic
element write can report this diagnostic and still produce its numeric
value. A bare arithmetic update through a projected ordinary element
nameref selects its stored subscript again for the write. A whole-name
write through a projected unset local nameref can reach a caller binding
while leaving the local nameref unset.

With an empty indexed projection, direct, arithmetic and default
element writes and scalar builtin element outputs create a fresh
indexed array under the authored global name. An enclosing local and
the marked array retain their bindings. Append starts with an empty
value, and a reached arithmetic element reads as zero. An authored
selector runs once per read or write. Whole-name `read -a` and
`mapfile` also recreate the global array. `mapfile -O` uses the
requested origin in that fresh array. For an existing projected
literal local, indexed input writes that binding. An absent projection
literal reports the complete spelling. Variable-only `unset -v` through
an empty projection removes the authored local and skips an element
selector.

Indexed input removes the nameref attribute from an uninitialized
terminal array and warns using the authored name. An associative
terminal then reports that indexed input requires an indexed array.
An initialized indexed array with no stored keys instead retains
its nameref attribute and reports the projected spelling.
Valid indexed nameref projections also select bindings for scalar,
element, aggregate and arithmetic reads, presence tests, length and
attribute inspection. A literal projection can read the local
binding with the complete element spelling. Bare indirect expansion
returns the last reference's spelling. A parameter default
assignment selects the destination after expanding the initializer.
With a missing literal, the bare-name default can assign to the
element described by the spelling. A default with an authored
selector diagnoses that missing literal after the selector effects.

Scalar builtin outputs follow a valid array nameref projection to its
receiver. A whole-name output uses an array's key zero or the element
described by a stored selector. If a projected literal local is absent,
the whole-name output can assign its base element. An authored element
selector uses the reached array kind. It reports the stored spelling
after its own effects when the projection supplies a stored element
reference or an absent literal. A reached `printf %n` write persists
when a later `printf -v` destination fails. An element reached by `read`
at EOF can receive an empty value while `read` returns status 1.

`export -a/-A` and `readonly -a/-A` filter listings when there are no
operands. A bare name receives the requested attribute and keeps its binding
kind. An assignment operand uses the global array declaration path, then
applies the named attribute to the visible binding. With both array flags,
this assignment path selects indexed storage. Parsed compound initializers
still use the existing source preparation phase. When a global indexed write
converts a shadowing associative binding, it keeps the binding's attributes.
Append uses the old value at key `0` as the previous element.

A simple whole-name initializer for a global indexed array applies its
declaration flags to the global binding and assigns element zero through
the active binding. This includes indexed arrays selected without `-a`.
An active local or temporary referent supplies the assignment's integer
and case attributes. A direct name reaches the caller binding behind its
temporary prefix. A nameref can reach the temporary binding itself.
An active element nameref or an unset nameref reports its target spelling
and leaves the global flags in place. The selector stays unevaluated.
A compound initializer parsed from an expanded operand stores the array
in its selected binding. Arithmetic reads the active prefix values and
retains its direct writes in the caller.

An explicit element assignment through `declare -g` prepares the global
array and applies the declaration attributes there. The write uses the
active binding, including its array kind, readonly state and nameref target.
An incompatible global array kind rejects the declaration before selector
evaluation. A quoted list with an explicit array kind initializes the
global array. With inferred typing, the quoted list is element text and
produces a warning.

The public script runner, `source`, `eval` and reached EXIT actions execute complete command units as
they are parsed. Earlier units retain their effects before later syntax failure.
A malformed arithmetic `for` header in a direct double-quoted command substitution
can also leave a complete prefix within the current unit. The shell emits the
diagnostic, publishes status 2, then executes that prefix and returns its status.
Inside an array initializer, the same failure discards the complete unit.
A recovered unmatched delimiter supplies a final diagnostic and status 1.
A nested header failure or a recovery scan with no delimiter error uses status 2.
With effective errexit, the streamed root, `source` and `eval` readers report
the first parser diagnostic and exit with status 2 before same-unit recovery. EXIT sees
the reader's active arguments, locals, descriptors and previous pipeline status.
A tested invocation suppresses this immediate exit. A streamed root grammar
error without errexit publishes its parser status before EXIT.
Explicit whole-script parsing remains available for callers that require validation
before execution.
Recoverable expansion failures discard the rest of their unit and expose the
failure status to the next unit. Readonly assignment failures and malformed
generated words stop execution when errexit applies at the script boundary.
The root policy also applies to failing commands in tested positions within a unit.
A tested `eval` or `source` invocation can recover within its nested units.
An invalid variable name in a complete for-in loop reports status 1 when the
loop is reached, before expanding its items. Prepared scripts use the same
execution rule. For-in item expansion and readonly binding diagnostics use the
ending line of the variable name. Missing loop syntax still fails during parsing.

`eval`, `source` and `.` consume one leading `--` from their expanded fields.
Other multi-byte dash options report status 2. A leading `--help` prints the
builtin's usage and returns 2, including when stdout is closed. Usage errors
return 2 when stderr is closed. The first error record includes the reached
line and any active source filename. The usage line follows without a location.
Later fields are ordinary operands.
`eval` joins its remaining fields with spaces and succeeds on empty input.
`source` and `.` require a filename and use any following fields as positional
parameters for that file. The filename must be valid UTF-8 for the host path
boundary. A filename containing a slash goes directly to the host. A bare
filename searches the current scalar `PATH` in order, then the working
directory. Empty `PATH` components select the working directory at their
position in the search. Search directories use the shell's supported tilde
prefixes. Open failures report the filename and host reason through the reached
stderr and return 1. The diagnostic includes the calling line and any active
source filename. A directory failure includes the builtin's spelling. An
explicitly empty filename clears the positional replacement decision and
reports the missing-file diagnostic during operand validation. A source call
without additional fields uses the caller's current parameters directly.
Given additional fields, a source call in a function
restores the function's parameters on return. At the top level, a reached `set`
can keep a replacement after an argument-bearing source returns. A later source
entry or in-process function return clears that replacement decision. A `shift`
alone leaves the restoration decision intact. Script content and arguments
retain their bytes.

An invalid braced parameter or empty subscript reports status 1 and ends the
reached command unit. It publishes `PIPESTATUS=(1)` in the active variable scope.
With errexit disabled, the reader continues at the next unit. A closed,
double-quoted word with an incomplete parameter subscript fails before its
substitutions run, while effects of earlier arguments survive. Source with a
missing closing brace fails parsing through the ordinary delimiter diagnostic.

Indirect value operators can read the source again before expanding the
operator's operands. The first indirect read establishes availability.
The second read selects the binding for the operator. A selected array can
supply different values, and a selected scalar can supply declaration
attributes for the initial payload. An empty short removal pattern returns
the initial payload after the first read. Plain indirection and default
operators use one source selection. A quoted scalar keeps one field when
the second read introduces an at aggregate. When an initial aggregate becomes a
scalar payload, empty fields contribute empty text and literal bytes remain
data.

A missing positional argument in `${!1}` or `${!12}` supplies an unset value.
Its usage as a field and the ordinary default and alternate operators follow
the same rules as for other unset positional arguments. An indirect assigning
default expands its operand before reporting `invalid indirect expansion`
for the missing argument. When the argument is present, its value supplies
the indirect name.
When a direct assigning default selects an unavailable special parameter,
it reports `cannot assign in this way` before expanding its operand.
These failures discard the current command unit. With errexit disabled,
the next unit of the root, eval or source reader sees status 1 and
`PIPESTATUS=(1)`. A required-parameter failure reports its expanded
message with the reached reader location and terminates that reader. An invalid
transformation of a present value reports `bad substitution` and takes the same
reader exit path. In a same-host `bash -c` command string, ordinary word expansion
uses exit status 127 when errexit is inactive and publishes `PIPESTATUS=(1)` in
the reached scope. This includes tested eval and source calls. A directly piped
builtin handles failures in its body with status 1. Explicit subshells and command
substitutions retain their own status rules. With errexit disabled, EXIT runs at
the command-string boundary or at the outer loaded reader after inner scopes
unwind. A command-substitution child unwinds the reader
invocation before EXIT. With errexit enabled, EXIT uses the reached scope and skips pending
timing reports. In pipeline stage preparation, these failures can reach the
inherited action during argument or prefix expansion. Redirection expansion
runs after that action has been reset.
An indirect read of a missing element is also unset when its base variable
exists, including a declaration without a value. A missing base variable,
an uninitialized nameref or an invalid indirect name reports an expansion
error. Invalid indirect assignments preserve effects reached before their
target is rejected. These errors discard the current command unit. With
errexit disabled, the next unit sees status 1, including inside `eval` and `source`.
A tested `eval` or `source` can recover in the same way under errexit.
A forked shell stops at the indirect error, including inside a loaded reader.

A successful default assignment through an array-element nameref writes to
the selected element and emits the array's scalar value after the write.
That value comes from index zero or associative key `0`.

A brace inside an array selector can belong to the selector when the same word
contains its closing bracket. A complete expansion then reads the selector with
that brace included. A selector can span a command substitution or adjacent
double-quoted segments within the word. A rejected capture in a bare selector
reports through the selector boundary. A quoted or nested capture in a selector
can report a subsequent parameter error during assignment or conditional
expansion. An unquoted reference with a complete selector can reach
the selector read before failing for a missing enclosing brace. Indexed effects
and selector diagnostics follow the indexed arithmetic recovery rules. A
parameter operator still requires an enclosing brace before its operand runs.

A rejected parameter-default write to a readonly binding or with a bad array
subscript retains the effects of its reached operand. Its diagnostic includes
the reached source line and the filename for a named sourced file. With errexit
disabled, the root reader continues at the next command unit with status 2. The failure
publishes `PIPESTATUS=(2)` in the reached binding scope. In-process `eval`
and `source` publish status 1 at their own unit boundary. A command capture
retains earlier bytes when it stops and
returns status 1 with errexit disabled. An errexit unwind preserves a prior
nonzero status and otherwise returns 1.

An ordinary assignment-target failure with errexit disabled ends the command
unit with status 1 and publishes `PIPESTATUS=(1)` before its binding scope
unwinds. In-process `eval`, `source` and EXIT action readers publish status 1
as they recover. When errexit terminates the write, a prior nonzero status and
the existing pipeline statuses survive. Forked execution ends at the failure.
Readonly assignment targets and list-to-member errors report the line at the
end of the command's first element. A named sourced file also supplies its path.
Assignment-target errors retain the lexical or resolved name selected by the
assignment form. `|&` stages use the original command's source coordinate.

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

A read that rejects a subscript after numeric evaluation reports the reached
source line. With errexit active, it stops before the command runs, preserves
`PIPESTATUS`, and exits with the previous nonzero status or 1. Testing the
command itself leaves this expansion rule in effect. A tested `eval` or `source`
suppresses it in the loaded reader. With errexit disabled, an ordinary read
treats the element as unset. A rejected length selector publishes status 1 and
`PIPESTATUS=(1)` in the reached scope before discarding its command unit.
An element length that starts from a scalar returns zero if the selector turns
that binding into an array. The selector's effects and invalid-subscript
diagnostics still apply.

When an arithmetic word takes its expansion pass, its array selectors expand
once. The indexed selector's brackets, apostrophes, and tildes remain escaped
for its numeric evaluation. An expansion elsewhere in the same arithmetic word
can select this path. Associative array keys retain their literal bytes.

`LINENO` follows the active parsed command. A simple command beginning with a
word uses the line reached while reading the next token for function-definition
lookahead. An initial assignment or redirection uses the line where that first
element ends. Rendered word-capture diagnostics and array-reader callbacks use
the same command coordinate. Functions defined in a script retain their defining
source coordinates. Sourced files and EXIT action source start at line 1, and
`eval` bases its coordinates on the calling command's parsed line.
When a simple command abandons its reader unit after rejected substitution syntax
in a here-document, the reader resumes line numbering from the reached command
line. Diagnostics and `LINENO` use this adjustment in the remaining input. A
loaded reader or EXIT action tracks its own adjustment. Functions keep their
definition coordinates, and a failure there leaves the caller's line count intact.
An unclosed backtick reports its suffix from the opening delimiter. An unclosed
parameter operand reports the document spelling. These word syntax failures
discard the unit, unwind its redirections and publish status 1 on recovery.
On errexit, EXIT runs with the reached descriptors. A missing brace exits with
status 1 while retaining the prior `PIPESTATUS` through EXIT. An unclosed
backtick publishes status 1 before EXIT.
Parenthesized captures project the command coordinate into the rendered body layout.
Backtick captures use the decoded body. Local and temporary bindings can shadow
`LINENO`. Unsetting the dynamic binding allows later assignments to create an
ordinary variable.

`BASH_ARGV0` initially exposes `$0`. A write to its dynamic scalar binding
sets the active shell name. A direct `BASH_ARGV0+=value` uses the
right-hand value. Appending through a declaration or a nameref combines this
value with the old one. An ordinary local or
temporary prefix can shadow the binding, and `local -I` can inherit it.
A subshell can change its own name independently. A fresh Bash invocation
initializes the binding from its launch name. Unset and array conversion keep
the last shell name and end the dynamic scalar binding.

`BASH_SUBSHELL` starts at zero in a new Bash invocation. Subshells,
command substitutions and pipeline stages advance the level. A
parenthesized pipeline stage counts once. A simple stage expands its
arguments and prefix assignment values at the parent level. Its
command redirections and body see the incremented level. A commandless
stage expands redirections at the parent level. The dynamic scalar
accepts decimal assignments. Local shadowing, unset and array conversion
leave the ordinary binding independent of the shell's current level.

The supported inherited `bash -c` launch initializes `BASH_EXECUTION_STRING`
from the original command argument bytes. It is an ordinary writable scalar.
An inherited export attribute survives the initialization. File and stream
readers keep any value supplied by the environment.

Invalid loop variable and function definition names use the reader's completed
unit line through the active stderr descriptor. A retained function uses its
body opening line for an invalid loop name and the definition header for an
invalid nested definition. A subshell enters a reader at its closing line.
`eval` counts these diagnostics from the last command word before trailing
redirections, and modern captures use their rendered reader layout.

An unhandled errexit status runs EXIT with the reached positional parameters and
loop or return target. An ordinary failure restores function locals and temporary
prefixes before the action. Subshell and pipeline child execution can keep those
bindings until it leaves the child scope. A simple failure restores the nearest
active simple invocation's redirections before EXIT. Compound command failures can
leave the reached redirection active. Timed failures report their timing before
the action. For a failing top-level brace group with a redirection, incremental
execution follows the pinned Bash script reader. Bash command-string invocation
can restore that mapping before EXIT.

When ordinary errexit in a nested reader reaches EXIT before its command
scopes unwind, the action restores the descriptor mappings changed in the reader
scopes that unwind. Other descriptors retain their reached mappings. In a command
substitution, an unwind across a function between reader entries can leave the
next enclosing reader's descriptor mappings active. This applies to readers
entered through a `mapfile` callback.
A command substitution also uses the selected reader unwind to determine which
function and source frames restore the action's state. Those unwinds consume
saved positional arguments in stack order.
A source with arguments can keep a replacement made by `set --` at global
scope. The action selects the return target and diagnostic filename separately
from the positional arguments.

An ordinary errexit failure inside a directly piped source or eval stage runs
EXIT with status 1 while `PIPESTATUS` retains the reached command's status.
The stage's redirections stay active. For a failing simple command, an inner
restorable invocation can unwind its own mappings before EXIT. Without a saved
invocation at global scope, the outer reader runs EXIT after its inner readers
unwind.

On ordinary pipeline completion, EXIT runs with the stage's active redirections.
A direct function stage retains its arguments, local bindings and return target.
A direct source stage restores the source arguments before the action. An
ordinary grammar error reached by a direct source or eval stage exposes 257 in
`PIPESTATUS` to EXIT and completes the stage with status 2.

EXIT actions preserve the triggering status on ordinary completion and parse failure
without errexit. Explicit return/exit and errexit failures can replace that status.
Parser diagnostics from the EXIT action reader carry the `exit trap:` prefix.
With errexit enabled when that reader reaches a syntax error, it reports the
first parser diagnostic and stops before any same-unit recovery.
Arithmetic/default expansion failures end their unit; assignment-target and parameter
failures stop an errexit-enabled action. Pending loop flow skips later execution while
still validating remaining units. Caller tested-status suppression does not apply to EXIT.

Arithmetic uses wrapping signed 64-bit values, with decimal, octal, hexadecimal and
explicit base-2-through-64 constants. `0x` and `0X` alone represent zero. An explicit
base has no leading zero. Stored variable/array values are arithmetic
expressions evaluated against current bindings; they are not re-expanded as shell
source. Active numeric recursion shares the structural nesting guard and observes
cancellation. Completed arithmetic work consumes no execution allowance.
A doubled sign before a variable name stays a mutation token in infix position,
including when blanks separate it from the name. An earlier postfix mutation
remains applied when the following token causes a syntax error.
When a value is followed by a reference without an operator, arithmetic checks
the reference and its following token before reading the binding. A reached
read can apply arithmetic effects before the enclosing expression reports its
syntax error. A lexical fault can stop a prefix write before it commits.
Arithmetic diagnostics report the reached source line and active `source`
filename. Parser errors preserve the failing operand and token bytes, including
trailing whitespace and physical LF in arithmetic `for` clauses. Arithmetic
commands, `let`, numeric conditionals and substring bounds identify the reached
caller for expression errors. Arithmetic and parameter defaults can write
through an initialized readonly nameref to a writable referent. The nameref's
attributes stay on its own binding. Readonly arithmetic diagnostics name the
selected binding. A spelled subscript or an implicit indexed element-zero
write names the operand. Readonly parameter defaults with a spelled
subscript also name the operand.

The substring offset scan finds the length delimiter outside balanced
parentheses. A stray closing parenthesis leaves the scan at the current level.
An unmatched opening parenthesis reports a bad substitution before either
bound runs. Its diagnostic keeps the operand bytes and renders parsed
command substitutions. This scan also checks comment-obscured capture closers
before the length delimiter. Inside an arithmetic group the scan checks
nested expansions in quoted portions of the group spelling. The length
operand expands when reached.
A trailing colon with no offset or length delimiter reports `bad substitution`
at the reached reader's line. A second colon allows an omitted offset. In an
expanded here-document, the diagnostic includes the document body spelling.
With errexit enabled at the root, the malformed offset terminates even a
tested command and the EXIT action observes status 1.
A reached negative length rejected by a substring range reports the spelling
of the length, including whitespace and quotes. It removes line continuations
and renders parsed command substitutions without running them again.

`let` consumes one leading `--` after operand expansion. A leading `--help`
requests help on stdout and returns status 2 even with closed stdout. Later
fields are arithmetic expressions. If no expressions remain, `let` reports
`expression expected` and returns status 1. The diagnostic includes the
reached line and any active source filename. A quoted empty expression
evaluates to zero and returns status 1 without a diagnostic.

Associative arrays enumerate keys in ascending lexicographic order of unsigned
bytes. Key and value aggregates follow that order, and value slices select
entries in the same order. GNU Bash can enumerate the same keys in a different
order.
An in-range associative value slice with length zero emits its selected entry.
Zero-length indexed-array and positional slices emit no entries.

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
octal escapes. In reusable single-quote form, a lone apostrophe is spelled
`\'`.

`test`/`[` preserve bytes in string equality, ordering and length predicates. Their
zero-to-four argument rules precede the `!`, parenthesis, `-a` and `-o` expression
grammar. In the expression grammar, `!` requires a following term. The short
argument-count rules can treat it as a literal string. In the three-argument
fallback, a first operand beginning with `(` and a third beginning with `)`
select the middle string. Numeric operands accept signed 64-bit decimal integers
with leading ASCII whitespace and trailing spaces or tabs allowed. Invalid
integers, malformed expressions and a missing closing `]` report a diagnostic
and status 2. The diagnostic uses the reached command's line and source file
when available. File predicates retain the UTF-8 path boundary.

For `-t`, a descriptor operand is a signed decimal with leading ASCII
whitespace and trailing spaces or tabs allowed. Only numbers in the
nonnegative descriptor range can reach the host. In the compound `test` and
`[` grammar, a final `-t` is a nonempty string term. If a token follows `-t`
in unary position, the predicate consumes it only when it is a signed 64-bit
decimal. A nonnumeric token stays in the expression and the predicate is false. Short-arity
`test` and `[` rules still take precedence. `[[ -t operand ]]` expands its
required operand and returns false for an invalid descriptor.

The unary `-o` predicate in `[[ … ]]`, `test` and `[` reports the current
`errexit` state and the always-enabled `braceexpand` and `pipefail` options.
All other option names return false.

`[[ … ]]`, `test` and `[` recognize `-v` and `-R`. An assigned empty value counts as
set. `-v` accepts variable names, array selectors and numeric positional parameters.
Positional indexes accept signed ASCII decimal with leading ASCII whitespace
and trailing spaces or tabs.
A bare array name queries element zero. For indexed arrays, `[@]` and `[*]` query
element presence across the array. For associative arrays, they denote the literal
keys `@` and `*`. `-R` tests whether the named binding is an initialized nameref,
including a reference to an unset target.
For an associative array, `[[ -v a[$'key'] ]]` checks `key` and
`[[ -v "a[$'key']" ]]` checks the literal `$key`. A reference produced as a
value uses the latter quote handling. When indexed arithmetic rejects the
resulting selector, the failed command unit ends with status 1.

Printf numeric operands use byte-oriented C-locale prefix parsing. Invalid suffixes
retain the converted prefix, report a byte-preserving diagnostic and return status 1;
range warnings preserve their successful conversion status. Character constants
consume one UTF-8 scalar or one malformed byte after the quote. Floating-point
conversion uses the deterministic 64-bit-significand extended profile, including
the pinned Linux hexadecimal spelling and NaN/subnormal behavior. A completed
NaN operand with an out-of-range numeric payload prefix prints a range warning
and keeps the converted NaN. A trailing invalid suffix selects the invalid-number
diagnostic. Exact subnormals do not produce range warnings. A shell-closed stderr
discards conversion diagnostics without suppressing stdout. Host-reported diagnostic
failures retain their ordinary stop policy. Numeric parsing observes cancellation
and shared workspace admission.

Usage failures from `printf`, `mapfile`, `readarray`, `read` and
`getopts` retain status 2 when stderr is explicitly closed by shell
redirection. The shell discards those diagnostics before reserving a record.
Closing stderr also suppresses `getopts` option warnings while the scan still
updates its option variables.
Write failures reported by the host still follow the builtin's status policy.

Input option and descriptor diagnostics from `read`, `mapfile` and
`readarray` report the reached source location on the first line. A
following usage line describes the supported options. Descriptor read
failures from `read` report the same source location and the logical
descriptor number.

`printf`, `read`, `mapfile` and `readarray` accept `--help` while scanning
options. Help describes the supported options and returns status 2, including
when the shell has closed stdout. An option that expects an argument consumes
its next field before the scan can reach help.
Readonly output errors from `read`, `mapfile`, `readarray`, `getopts` and
`printf` report the reached builtin line and any active `source` filename.
Whole-array input reports the written destination, while resolved scalar
and element assignments report the readonly binding's name.
Invalid destination identifiers and whole-array destinations of the wrong
kind use those same coordinates. Scalar `read` validates the first
destination before reading and checks later destinations as it assigns
fields. `read -a` validates the array destination after reading, while
`mapfile` and `readarray` validate it before reading.

When a reached `printf -v` has no destination field, it reports that the
option requires an argument and follows with usage. A supplied destination
with no format reports usage alone.
Option errors, invalid destination names and format diagnostics report
the reached builtin line and any active `source` filename. Usage lines
have no source prefix. An unfinished conversion quotes its full spelling
from the opening percent sign.
When `echo` or `printf` output fails, the write diagnostic uses the
reached source location and stderr mapping.
`printf -v` assigns the bytes before the first NUL. Later formatted bytes
still contribute to `%n` and the output length limit. Later conversions
retain their normal diagnostic and count-assignment effects.
`%q` and `%Q` use ANSI-C spelling for nonprinting or malformed bytes.
`%q` limits the quoted output by its precision. For `%Q`, literal precision
limits the operand before quoting, and `*` precision consumes and validates
its argument without limiting the operand.
The backslash form protects commas so the quoted result can be reused in a
brace list. The `#` flag selects the Bash 5.3 single-quote form for printable
bytes.

`unset` follows the same help status and output rules. Leading option groups
are scanned in order. An earlier invalid option reports usage, and `--`
ends the option scan. Reached help preserves the operand bindings.
Invalid options, conflicting namespace options and invalid `-v` operands
report the reached line and any active source filename. The usage line
after an invalid option carries just the builtin synopsis.
`unset -v` accepts identifiers, valid array selectors and existing literal
bindings. Invalid operands report their original bytes and set status 1 while
later operands still run. An empty operand to plain `unset` has no effect.
For an element selector that reaches a scalar, `unset` selects the scalar
policy before evaluating the index. A zero result removes the whole binding,
including an array created by the selector. A nonzero result preserves the
selector's effects. A direct operand returns status 1 and reports the resolved
name if it is still a scalar. An element nameref returns status 0 for ordinary
removal failures, including an invalid array subscript. A fatal expansion error
still applies the reader's failure rules. Reached subscript diagnostics include
the builtin's source line and the original selector spelling.
A subscripted operand through an element nameref selects the element recorded
in the reference and evaluates its stored selector once. It retains the
reference binding. Readonly protection applies before selector evaluation.
When a nameref chain ends at an uninitialized reference, ordinary variable
removal selects the originally named reference and preserves the terminal
declaration. Function fallback still applies before that removal.
`unset -n` selects the named reference for removal. A readonly last reference
with an assigned target blocks removal and supplies the diagnostic name.
For a projected array nameref, whole-variable removal selects the reached
binding, including a literal local. Element removal uses the reached array
kind or a stored element selector. Plain `unset` can fall back to a
same-named function when the projection has no whole binding. With a
subscripted operand in that case, it uses the authored name as a scalar
and leaves the function in place. Readonly protection also applies to
that scalar selection.
Readonly variable and function diagnostics use the reached builtin's source
line and include the filename for a named sourced file.
Lokad.Bash also preserves a readonly reference when the command would
remove that binding by its own name. Both pinned GNU Bash builds can remove
the named readonly reference when a later writable reference supplies
the check, or when the named reference is uninitialized.

`help --help` uses the same output and status rules. A list of topics succeeds
when any topic matches. If all topics miss, status 1 accompanies a diagnostic
on stderr that preserves the final topic's bytes. The topic catalog and help
records describe the builtins supported by Lokad.Bash.

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
signed 64-bit wrapping. `%Q` uses a signed 32-bit literal precision
accumulator before quoting. The minimum dynamic width leaves empty formatted
values unpadded.
Padding for nonempty values is subject to the ordinary output representation
and budget limits.

Printf time-format literals preserve bytes, including malformed UTF-8. Directives
and widths use ASCII grammar with invariant date text. The pinned Linux C
locale governs which directives accept the `E` and `O` modifiers and how
they apply `^` and `#`. Unknown and incomplete directives retain their literal
spelling with the parsed width and applicable ASCII case. Explicit seconds use
the configured fixed offset with a Gregorian calendar. The calendar admits
local years whose difference from 1900 fits in a signed 32-bit field. Within
that range `%s` retains the supplied seconds. Numeric year rendering follows
the pinned Linux profile, including its signed 32-bit year display. Ordinary
numeric directives keep a digit when the
`_` flag selects space padding. The `-` flag suppresses default numeric padding
and still honors an explicit width. Composite directives apply width to the
complete value and retain their internal padding. The `%z` offset applies an
explicit width separately to the sign field and to the digits. Its default
output uses four zero-padded digits after the sign. For `%Z`, the fixed-offset
profile uses `UTC` at zero and a signed `HH:MM` offset elsewhere. An out-of-range
operand falls back to the Unix epoch at the configured offset. The missing,
`-1` and `-2` operands
select the configured timestamp. Each `%(...)T` conversion uses GNU's 128-byte
buffer profile: a result requiring 128 or more bytes becomes an empty value with
successful status. Outer field width and precision apply afterward. This conversion
rule is distinct from shared-memory refusal, which remains a reported failure.

Hosts provide external commands, including any `sh`/`bash` aliases and script-file
launch policy. Command names and filesystem capabilities are never inferred from
the ambient machine. Additional host tools do not expand the language grammar.
