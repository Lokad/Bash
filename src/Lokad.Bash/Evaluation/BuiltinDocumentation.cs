using System;

namespace Lokad.Bash.Evaluation;

/// <summary>Describes the implemented builtin surface, shared by help and usage diagnostics.</summary>
internal readonly record struct BuiltinDocumentation(string Arguments, string Description)
{
    public string Summary
    {
        get
        {
            var end = Description.IndexOf('\n');
            return end < 0 ? Description : Description[..end];
        }
    }

    public string Synopsis(string name) => name == "[" ? "[ expression ]"
        : Arguments.Length == 0 ? name : name + " " + Arguments;

    public static BuiltinDocumentation For(BuiltinKind kind) => kind switch
    {
        BuiltinKind.Colon => new("[arguments]", "Expand arguments and redirections, then return success."),
        BuiltinKind.True => new("", "Return success."),
        BuiltinKind.False => new("", "Return failure."),
        BuiltinKind.Exit => new("[n]", "Exit this shell evaluation with status n, or the last command's status."),
        BuiltinKind.Trap => new("[-p] [--] [action condition ...]",
            "Register, reset or list shell cleanup handlers. Conditions: EXIT/0, HUP/1, INT/2, TERM/15.\n    EXIT runs on normal shell completion or explicit exit, with the original status in $?. '-' resets a handler; an empty action ignores it.\n    -p lists registrations as reusable commands. Signal registrations are local; external signals and host cancellation do not deliver them."),
        BuiltinKind.Break => new("[n]", "Leave n enclosing loops; n defaults to 1."),
        BuiltinKind.Continue => new("[n]", "Continue the next iteration of the nth enclosing loop; n defaults to 1."),
        BuiltinKind.Return => new("[n]", "Return from a function or sourced script with status n, or the last command's status."),
        BuiltinKind.Let => new("expression [expression ...]", "Evaluate integer arithmetic expressions; return success if the last value is nonzero."),
        BuiltinKind.Test => new("expression", "Evaluate string, integer and file predicates; return 0 for true, 1 for false, or 2 for invalid syntax."),
        BuiltinKind.Pwd => new("", "Print the current working directory."),
        BuiltinKind.Cd => new("dir", "Change the working directory to dir; '-' selects the previous directory."),
        BuiltinKind.Echo => new("[-n] [arg ...]", "Print arguments separated by spaces; -n omits the final newline."),
        BuiltinKind.Printf => new("[-v var] format [arguments]", "Format and print arguments, or assign the result to var with -v."),
        BuiltinKind.Read => new("[-r] [-d delim] [-u fd] [name ...]",
            "Read one record and split it into fields using IFS; without names, assign REPLY.\n    -d uses the first byte of delim instead of newline; an empty delim selects NUL.\n    -r preserves backslashes; -u fd reads from an open descriptor. Return failure at EOF."),
        BuiltinKind.Unset => new("[-f] [-v] [-n] [name ...]",
            "Remove variables or functions. -f selects functions, -v variables, and -n removes a name reference itself."),
        BuiltinKind.Dirs => new("[-c]", "Print the directory stack; -c clears it to the current directory."),
        BuiltinKind.Pushd => new("[dir]", "Push dir onto the directory stack, or swap its top two entries when dir is omitted."),
        BuiltinKind.Popd => new("", "Remove the top directory stack entry and change to the next directory."),
        BuiltinKind.Export => new("[-aAfnp] [name[=value] ...]",
            "Mark bindings for export, or list exported bindings. -n removes export; -f selects functions; -a/-A select array kinds."),
        BuiltinKind.Readonly => new("[-aAfp] [name[=value] ...]",
            "Mark bindings readonly, or list readonly bindings. -f selects functions; -a and -A select indexed and associative arrays."),
        BuiltinKind.Mapfile => new("[-t] [--] [array]",
            "Read standard input into an indexed array, defaulting to MAPFILE. -t removes each record's trailing newline."),
        BuiltinKind.Hash => new("", "Return success without operands; command-path caching is not implemented."),
        BuiltinKind.Shift => new("[n]", "Remove the first n positional parameters; n defaults to 1."),
        BuiltinKind.Set => new("[-e|+e] [-o errexit|+o errexit] [--] [arg ...]", "Set errexit or replace positional parameters. -o/+o alone inspect errexit. Unsupported options report an error. With no arguments, return success without listing variables."),
        BuiltinKind.Exec => new("[-cl] [-a name] [command [argument ...]] [redirection ...]",
            "Replace this evaluation with a command, or preserve redirections without a command."),
        BuiltinKind.Declare => new("[-aAfgFnprx] [name[=value] ...]",
            "Declare or list variables and functions. -a/-A select array kinds, -n a name reference, -r readonly, and -x export.\n    -g selects global bindings; -f/-F list function definitions/names; -p prints declarations. '+' removes attributes."),
        BuiltinKind.Local => new("[-aAfFnprx] [name[=value] ...]",
            "Declare function-local variables, arrays or name references; declaration attributes use the same flags as declare except -g."),
        BuiltinKind.Caller => new("", "Print the current function context; return failure outside a function."),
        BuiltinKind.Source => new("filename [arguments]", "Read and execute filename in the current shell context, with optional positional parameters."),
        BuiltinKind.Eval => new("[arg ...]", "Join arguments with spaces, then parse and execute them in the current shell context."),
        BuiltinKind.Getopts => new("optstring name [arg ...]", "Parse separate short flag arguments into name and advance OPTIND; use positional parameters when arguments are omitted.\n    Bundled flags, required option arguments, OPTARG lifecycle and GNU diagnostic modes are unsupported."),
        BuiltinKind.Command => new("[-pVv] command [arg ...]", "Run a command without function lookup, or describe its resolution."),
        BuiltinKind.Type => new("[-t] [--] name [name ...]", "Describe how names resolve; -t prints function, builtin or file."),
        BuiltinKind.Builtin => new("[shell-builtin [arg ...]]", "Execute a supported shell builtin, bypassing functions."),
        BuiltinKind.Help => new("[-dms] [pattern ...]",
            "Display information about supported shell builtins. With no patterns, list builtin topics.\n    Patterns support *, ? and bracket ranges, with backslash escaping; if none match, try name prefixes.\n    -d prints descriptions, -s usage synopses, and -m a manual-style page.\n    Return 0 if any topic matches, 1 if none match, or 2 for invalid options or --help."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
