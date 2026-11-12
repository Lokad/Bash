# Changelog

## Unreleased

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
