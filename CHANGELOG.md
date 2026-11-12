# Changelog

## Unreleased

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
