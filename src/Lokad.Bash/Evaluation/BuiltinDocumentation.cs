using System;

namespace Lokad.Bash.Evaluation;

/// <summary>Describes the implemented builtin surface, shared by help and usage diagnostics.</summary>
internal readonly record struct BuiltinDocumentation(string Arguments, string Description)
{
    private const string CaseAttributeDescription =
        "\n    -l lowercases assigned values, -u uppercases them, and -c uppercases the first character and lowercases the rest.";

    public ReadOnlyMemory<char> Summary
    {
        get
        {
            var end = Description.IndexOf('\n');
            return end < 0 ? Description.AsMemory() : Description.AsMemory(0, end);
        }
    }

    internal string SynopsisArguments(string name) => name == "[" ? "expression ]" : Arguments;

    public string Synopsis(string name)
    {
        var arguments = SynopsisArguments(name);
        return arguments.Length == 0 ? name : name + " " + arguments;
    }

    internal static string DeclarationOptionSynopsis(BuiltinKind kind, string name) => kind switch
    {
        BuiltinKind.Readonly => "readonly [-aAf] [name[=value] ...] or readonly -p",
        BuiltinKind.Export => "export [-fn] [name[=value] ...] or export -p",
        BuiltinKind.Local => "local [option] name[=value] ...",
        BuiltinKind.Declare => name == "typeset"
            ? "typeset [-aAfFgiIlnrtux] name[=value] ... or typeset -p [-aAfFilnrtux] [name ...]"
            : "declare [-aAfFgiIlnrtux] [name[=value] ...] or declare -p [-aAfFilnrtux] [name ...]",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

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
        BuiltinKind.Let => new("arg [arg ...]", "Evaluate integer arithmetic expressions. Return success if the last value is nonzero."),
        BuiltinKind.Test => new("expression", "Evaluate string, integer and file predicates; return 0 for true, 1 for false, or 2 for invalid syntax."),
        BuiltinKind.Pwd => new("[-LP]", "Print the current working directory. Both -L and -P print the directory tracked by the host model."),
        BuiltinKind.Cd => new("[-Le] [--] [dir]", "Change the working directory to dir, or to HOME when omitted. A lone '-' selects and prints OLDPWD."),
        BuiltinKind.Echo => new("[-neE ...] [arg ...]",
            "Print arguments separated by spaces. Leading option groups may repeat or combine n, e and E.\n    -n omits the final newline. The last -e or -E enables or disables backslash escapes, which are disabled by default."),
        BuiltinKind.Printf => new("[-v var] format [arguments]", "Format and print arguments, or assign the result to var with -v."),
        BuiltinKind.Read => new("[-ers] [-a array] [-d delim] [-i text] [-n count] [-N count] [-p prompt] [-u fd] [name ...]",
            "Read one record. With no destination, assign the whole record to REPLY.\n"
            + "    Split fields using IFS for named destinations. -a replaces an indexed array with all split fields.\n"
            + "    -d uses the first byte of delim instead of newline; an empty delim selects NUL.\n"
            + "    -n stops at the byte count or delimiter. -N reads to the count without delimiter or field splitting.\n"
            + "    -r preserves backslashes; -u fd reads from an open descriptor. Return failure on early EOF.\n"
            + "    -e, -i text, -p prompt and -s are accepted for non-interactive input without terminal effects."),
        BuiltinKind.Unset => new("[-f] [-v] [-n] [name ...]",
            "Remove variables or functions. -f selects functions, -v variables, and -n removes a name reference itself."),
        BuiltinKind.Dirs => new("[-clpv] [+N] [-N]",
            "Print the directory stack. The -c option clears it without printing.\n    -l prints full paths. -p prints one entry per line. -v also prints each index.\n    +N counts from the top and -N from the bottom, starting at zero. Supply options separately."),
        BuiltinKind.Pushd => new("[-n] [+N | -N | dir]", "Push or rotate the directory stack. With no operand, swap the top entries. A navigation operand of '-' selects OLDPWD. The -n option keeps the working directory."),
        BuiltinKind.Popd => new("[-n] [+N | -N]", "Remove a directory stack entry. The -n option keeps the working directory."),
        BuiltinKind.Export => new("[-aAfnp] [name[=value] ...]",
            "Mark bindings for export, or list exported bindings. -n removes export; -f selects functions; -a/-A select array kinds."),
        BuiltinKind.Readonly => new("[-aAfnp] [name[=value] ...]",
            "Mark bindings readonly, or list readonly bindings. -f selects functions; -a and -A select indexed and associative arrays. With named operands, -n skips adding the readonly attribute and preserves existing attributes."),
        BuiltinKind.Mapfile => new("[-t] [-d delim] [-n count] [-s count] [-O origin] [-u fd] [-C callback] [-c quantum] [--] [array]",
            "Read records into an indexed array. The default destination is MAPFILE.\n"
            + "    Read standard input unless -u selects a descriptor. -d uses the first delimiter byte, or NUL for an empty delimiter. -t removes each record's trailing delimiter.\n"
            + "    -s skips the first count records. The -n limit applies to records after the skip. With -n 0, read to EOF. -O writes at the supplied starting index and preserves other entries.\n"
            + "    -C evaluates a callback before every quantum-th record is assigned, appending the index and record as arguments. -c sets the positive quantum, which defaults to 5000."),
        BuiltinKind.Hash => new("[-dlrt] [--] [name ...]",
            "Report an empty command cache. -l lists its reusable entries and -r clears it. Both succeed silently.\n    -d deletes named entries and succeeds against the empty cache. -t reports each queried name as missing."),
        BuiltinKind.Shift => new("[--] [n]", "Remove the first n positional parameters. With no count, remove one."),
        BuiltinKind.Set => new("[-ef|+ef] [-o name|+o name] [-|+|--] [arg ...]", "Set errexit or noglob, or replace positional parameters. A lone + is consumed while scanning options. -o/+o alone inspect the supported mutable options. With no arguments, list variable assignments and function definitions."),
        BuiltinKind.Shopt => new("[-pqsu] [-o] [optname ...]",
            "Inspect or change shell options. Supported options are dotglob, failglob, globskipdots,\n    inherit_errexit, localvar_inherit, nocaseglob, nocasematch, and nullglob. -s enables,\n    -u disables, -p prints reusable settings, and -q suppresses listings.\n    -o inspects set options or changes errexit."),
        BuiltinKind.Exec => new("[-cl] [-a name] [command [argument ...]] [redirection ...]",
            "Replace this evaluation with a command, or preserve redirections without a command."),
        BuiltinKind.Declare => new("[-aAcfFgiIlnprux] [name[=value] ...]",
            "Declare or list variables and functions. -a/-A select array kinds, -i evaluates assigned values as integers, -n selects a name reference, -r marks readonly, and -x exports.\n    -I inherits an outer binding in a function. -g selects global bindings. -f/-F list function definitions/names. -p prints declarations. '+' removes attributes."
            + CaseAttributeDescription),
        BuiltinKind.Local => new("[-aAcfFiIlnprux] [name[=value] | -] ...",
            "Declare function-local variables, arrays or name references. -I inherits the outer value and its attributes, except the name-reference attribute. '-' saves shell options for restoration when the function returns.\n    Declaration attributes use the same flags as declare except -g."
            + CaseAttributeDescription),
        BuiltinKind.Caller => new("[expr]", "Print the call line and source. A frame index also includes the caller name."),
        BuiltinKind.Source => new("filename [arguments]", "Read and execute filename in the current shell context, with optional positional parameters. A leading -- ends option parsing."),
        BuiltinKind.Eval => new("[arg ...]", "Join arguments with spaces, then parse and execute them in the current shell context. A leading -- ends option parsing."),
        BuiltinKind.Getopts => new("optstring name [arg ...]",
            "Parse one short option from the supplied arguments or positional parameters into name.\n    A colon after an option requires an argument, stored in OPTARG. Set OPTIND=1 to restart the scan.\n    A leading colon selects silent errors. Set OPTERR=0 to suppress diagnostics without changing error results."),
        BuiltinKind.Command => new("[-pVv] command [arg ...]", "Run a command without function lookup, or describe its resolution."),
        BuiltinKind.Type => new("[-aftpP] [--] [name ...]", "Describe how names resolve. -a lists all matching shell definitions and the host result. -t prints the kind. -p prints the path of an external command. -P forces host lookup. -f skips shell functions."),
        BuiltinKind.Builtin => new("[shell-builtin [arg ...]]", "Execute a supported shell builtin, bypassing functions."),
        BuiltinKind.Help => new("[-dms] [pattern ...]",
            "Display information about supported shell builtins. With no patterns, list builtin topics.\n    Patterns support *, ? and bracket ranges, with backslash escaping; if none match, try name prefixes.\n    -d prints descriptions, -s usage synopses, and -m a manual-style page.\n    Return 0 if any topic matches, 1 if none match, or 2 for invalid options or --help."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
