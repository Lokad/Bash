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

Pipeline status uses fixed `pipefail`: the rightmost failing stage wins. Scripts
are limited to 4 MiB, with additional syntax, nesting and cumulative expansion
limits. Pending here-documents followed by a pipeline/list operator before the
body are not fully supported. Builtins implement their documented option subsets;
`set` sets positional parameters and does not toggle shell options.

Regular expression conditions currently use .NET regular expressions rather than
claiming full POSIX ERE compatibility. Each match has a 250 ms backtracking timeout;
refusal follows the conditional expression error path. The host cancellation token
does not interrupt an individual .NET regex call before that timeout.

Out of scope: interactive prompts/history/completion, aliases, background execution,
job/process-group control, OS signal handling, process substitution, `coproc`,
`select`, network redirection devices, `ulimit`, `umask` and shell option toggles.
The runtime does not import process environment variables or startup files.

Hosts provide external commands, including any `sh`/`bash` aliases and script-file
launch policy. Command names and filesystem capabilities are never inferred from
the ambient machine. Additional host tools do not expand the language grammar.
