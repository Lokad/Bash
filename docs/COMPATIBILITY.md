# Compatibility

The runtime supports deterministic, non-interactive shell workflows:

- Lists, `&&`, `||`, groups, subshells, functions, loops, conditionals and pipelines.
- Scalar, sparse indexed and associative arrays, namerefs and positional parameters.
- Quoting, ANSI-C quoting, brace/tilde/parameter/arithmetic/command expansion,
  word splitting, pathname expansion and quote removal.
- Redirections, here-documents, here-strings, logical descriptor aliases and
  allocated descriptors. `exec` can preserve descriptors or replace evaluation.
- Control, directory, variable, input and formatting builtins, including `read`,
  `mapfile`, `printf -v`, `source`, `eval`, `command`, `type`, `getopts` and `help`.
- `time`, host-mediated filesystem predicates, per-shell trap registration and EXIT
  handlers. Cancellation and external signals do not deliver traps.

Source and shell values preserve bytes, including invalid UTF-8 values. Text-only
host boundaries validate UTF-8. Only LF terminates a source line; CR is data.
Command substitution removes NUL bytes and trailing LF bytes while retaining status.
File paths use the contained POSIX-style namespace defined by `ShellPath` and
`FilePathLookup`, including their size, control-character and traversal constraints.

The deterministic character profile is UTF-8 with invariant character operations.
Length and substring operations count Unicode scalar values and individual malformed
bytes. Shell-word patterns preserve quoting and match scalars when both operands
are valid UTF-8; a malformed pattern or value selects byte matching, as in GNU's
UTF-8 locale. Removal slices the selected bytes directly and can therefore produce
malformed byte values without passing through a text encoder. Pathname operations
retain their separate text-only host boundary and path component policies.

Pipeline status uses fixed `pipefail`: the rightmost failing stage wins. The
indexed `PIPESTATUS` array exposes individual foreground statuses.
The array records statuses before pipeline negation; a subsequent command expands
its operands against the previous array and replaces it when it completes. Empty
loops and function definitions preserve the previous array.
Scripts are limited to 4 MiB, with structural syntax/nesting guards and CLR collection-size
checks. The source bound limits parser input; nesting guards protect recursive
syntax handling. They are independent of execution duration. There are no lifetime
word, character, brace, argument-copy or pattern-step quotas. Callers supply
cancellation and a live-memory allowance. Pending here-documents followed by a
pipeline/list operator before the body are not fully supported. Builtins implement
their documented option subsets;
`set` sets positional parameters and supports `-e`/`+e` and
`-o errexit`/`+o errexit`. Errexit stops unhandled failures, including failed
pipelines under the fixed pipefail policy. Tested conditions, logical-list
positions and negation suppress it through nested functions/groups and source/eval.
Command substitution clears errexit by default; parenthesized subshells inherit it.
`$-` and `${-}` expose the supported mutable flags. `set -o`/`set +o` inspect
errexit only; other option changes return an error with a diagnostic.

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

Extended shell patterns `?(...)`, `*(...)`, `+(...)`, `@(...)` and `!(...)` are
unsupported in `[[`, case arms and parameter pattern operators. Case arms support
the `;;` terminator; `;&` fallthrough and `;;&` retesting are unsupported.

Out of scope: interactive prompts/history/completion, aliases, background execution,
job/process-group control, OS signal handling, process substitution, `coproc`,
`select`, network redirection devices, `ulimit`, `umask` and shell option toggles
other than errexit.
The runtime does not import process environment variables or startup files.

The public script runner, `source` and `eval` execute complete command units as
they are parsed. Earlier units retain their effects before later syntax failure;
the failing unit executes no partial command list. Explicit whole-script parsing
remains available for callers that require validation before execution.

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

Implicit `for name; do ...; done` iterates a snapshot of quoted positional
parameters, preserving empty arguments and bytes. Explicit `in` lists retain their
usual expansion rules. `exit` accepts signed 64-bit decimal statuses modulo 256;
invalid numbers and surplus arguments diagnose and stop non-interactive execution.
Outside a loop, `break`/`continue` warn and return 0. Within loops, nonpositive counts
abandon all active loops with status 1; malformed counts and surplus operands have
their distinct shell-exit statuses (128 and 1).

Hosts provide external commands, including any `sh`/`bash` aliases and script-file
launch policy. Command names and filesystem capabilities are never inferred from
the ambient machine. Additional host tools do not expand the language grammar.
