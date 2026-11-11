using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class DeclarationFunctionTestsEndToEnd
{
    // Git Bash 5.2.37 in an empty environment; omit only native script-path/line diagnostic prefixes.
    [Theory]
    [InlineData("count=0; f(){ printf executed; ((count+=1)); }; declare -f f; type f; printf 'count=%s\\n' \"$count\"", "f () \n{ \n    printf executed;\n    ((count+=1))\n}\nf is a function\nf () \n{ \n    printf executed;\n    ((count+=1))\n}\ncount=0\n", "", 0)]
    [InlineData("f(){ :; }; export -f f; readonly -f f; declare -f f; declare -fp f; declare -f; export -f; readonly -f", "f () \n{ \n    :\n}\nf () \n{ \n    :\n}\ndeclare -frx f\nf () \n{ \n    :\n}\ndeclare -frx f\nf () \n{ \n    :\n}\ndeclare -frx f\nf () \n{ \n    :\n}\ndeclare -frx f\n", "", 0)]
    [InlineData("function if { :; }; declare -f if; type if", "function if () \n{ \n    :\n}\nif is a shell keyword\n", "", 0)]
    [InlineData("f(){ :; }; export -f f; readonly -f f; declare -f f; declare -fp f; export -fp f; readonly -fp f; type f", "f () \n{ \n    :\n}\nf () \n{ \n    :\n}\ndeclare -frx f\nf is a function\nf () \n{ \n    :\n}\n", "", 0)]
    [InlineData("probe_z(){ :; }; probe_a(){ :; }; declare -F probe_z missing probe_a; printf 'status=%s\\n' \"$?\"; declare -F", "probe_z\nprobe_a\nstatus=1\ndeclare -f probe_a\ndeclare -f probe_z\n", "", 0)]
    [InlineData("declare -F; printf 'status=%s\\n' \"$?\"", "status=0\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare -Fp probe_z; declare -pF probe_z; declare -Ff probe_z", "declare -f probe_z\ndeclare -f probe_z\nprobe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare -F +F probe_z; declare +F -F probe_z; declare +f -F probe_z; declare -F +f probe_z; declare -F +F", "declare -f probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare -Fx probe_z; declare -Fp probe_z; declare -Fx", "declare -fx probe_z\ndeclare -fx probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare -Fr probe_z; declare -Fp probe_z; declare -Fr", "declare -fr probe_z\ndeclare -fr probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare -frx probe_z; declare -Fp probe_z; declare +fxr probe_z; declare -Fp probe_z", "declare -frx probe_z\ndeclare -frx probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; probe_a(){ :; }; probe_b(){ :; }; export -f probe_z; readonly -f probe_a; declare -Fx; declare -Fr; declare -Frx; declare -F", "declare -fx probe_z\ndeclare -fr probe_a\ndeclare -fr probe_a\ndeclare -fx probe_z\ndeclare -fr probe_a\ndeclare -f probe_b\ndeclare -fx probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; probe_a(){ :; }; export -f probe_a; declare -Fxp probe_z; declare -Fp probe_z; declare -Frp probe_a; declare -Fp probe_a", "declare -f probe_z\ndeclare -f probe_z\ndeclare -fx probe_a\ndeclare -fx probe_a\n", "", 0)]
    [InlineData("probe_z(){ :; }; export -f probe_z; export -fn probe_z; declare -Fp probe_z", "declare -f probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; readonly -f probe_z; declare +fr probe_z; printf 'status=%s\\n' \"$?\"; declare -Fp probe_z", "status=0\ndeclare -fr probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare -F 'probe_z=value' missing probe_z; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\ndeclare -f probe_z\n", "declare: cannot use `-f' to make functions\n", 0)]
    [InlineData("declare -F '' 'bad-name' 'a[1]' missing; printf 'status=%s\\n' \"$?\"", "status=1\n", "", 0)]
    [InlineData("declare -fx missing; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\n", "", 0)]
    [InlineData("probe_z(){ :; }; export -f missing probe_z; printf 'status=%s\\n' \"$?\"; declare -Fp probe_z", "status=1\ndeclare -fx probe_z\n", "export: missing: not a function\n", 0)]
    [InlineData("probe_z(){ :; }; f(){ local -F probe_z; local -F; }; f", "probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; f(){ local -Fx probe_z; }; f; declare -Fp probe_z", "declare -fx probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare -Fa probe_z; printf 'status=%s\\n' \"$?\"; declare -FA probe_z; printf 'status=%s\\n' \"$?\"; declare -Fn probe_z; printf 'status=%s\\n' \"$?\"; declare -Fp probe_z", "status=1\nstatus=1\nstatus=1\ndeclare -f probe_z\n", "declare: -a: invalid option\ndeclare: -A: invalid option\ndeclare: -n: invalid option\n", 0)]
    [InlineData("probe_z(){ :; }; declare +af probe_z; printf 'status=%s\\n' \"$?\"; declare +nf probe_z; printf 'status=%s\\n' \"$?\"", "status=0\nstatus=0\n", "", 0)]
    [InlineData("probe_z(){ printf old; }; readonly -f probe_z; probe_z(){ printf new; }; printf 'status=%s\\n' \"$?\"; probe_z", "status=1\nold", "probe_z: readonly function\n", 0)]
    [InlineData("probe_z(){ :; }; export -f probe_z; probe_z(){ printf new; }; declare -Fp probe_z; probe_z", "declare -fx probe_z\nnew", "", 0)]
    [InlineData("probe_z(){ :; }; (readonly -f probe_z; declare -Fp probe_z); declare -Fp probe_z", "declare -fr probe_z\ndeclare -f probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; value=$(export -f probe_z; declare -Fp probe_z); printf '%s\\n' \"$value\"; declare -Fp probe_z", "declare -fx probe_z\ndeclare -f probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; readonly -f probe_z | cat; declare -Fp probe_z", "declare -f probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; f(){ readonly -f probe_z; }; x=prefix f; declare -Fp probe_z", "declare -fr probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; probe_z=value; readonly probe_z; export -f probe_z; declare -Fp probe_z; declare -p probe_z", "declare -fx probe_z\ndeclare -r probe_z=\"value\"\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare +Fp probe_z; declare -F +p probe_z", "declare -f probe_z\n", "declare: probe_z: not found\n", 0)]
    [InlineData("probe_z(){ :; }; i=0; declare -Fx \"probe_$((i+=1))\" probe_z; printf 'i=%s status=%s\\n' \"$i\" \"$?\"; declare -Fp probe_z", "i=1 status=1\ndeclare -fx probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare +f probe_z missing; declare -p probe_z missing; declare -F probe_z missing; printf 'status=%s\\n' \"$?\"", "declare -- probe_z\ndeclare -- missing\nprobe_z\nstatus=1\n", "", 0)]
    [InlineData("probe_z(){ :; }; declare +f -F probe_z; printf 'status=%s\\n' \"$?\"; declare -p probe_z", "status=0\n", "declare: probe_z: not found\n", 1)]
    [InlineData("probe_z(){ :; }; declare -F +f probe_z; printf 'status=%s\\n' \"$?\"; declare -p probe_z", "status=0\n", "declare: probe_z: not found\n", 1)]
    [InlineData("probe_z(){ :; }; export -f probe_z; declare -f +x probe_z; declare -Fp probe_z", "declare -f probe_z\n", "", 0)]
    [InlineData("probe_z(){ :; }; readonly -f probe_z; declare -f +r probe_z; printf 'status=%s\\n' \"$?\"; declare -Fp probe_z", "status=1\ndeclare -fr probe_z\n", "declare: probe_z: readonly function\n", 0)]
    [InlineData("probe_a(){ :; }; probe_b(){ :; }; probe_c(){ :; }; readonly -f probe_a; export -f probe_b; declare -Frx", "declare -fr probe_a\ndeclare -fx probe_b\n", "", 0)]
    [InlineData("probe_z(){ :; }; f(){ local probe_v=value; local -F; local -f; }; f", "declare -- probe_v\ndeclare -- probe_v=\"value\"\n", "", 0)]
    [InlineData("probe_a(){ :; }; probe_z(){ :; }; declare -Fx probe_a 'x=value' probe_z; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\ndeclare -fx probe_a\ndeclare -f probe_z\n", "declare: cannot use `-f' to make functions\n", 0)]
    [InlineData("probe_z(){ printf old; }; readonly -f probe_z; ! probe_z(){ printf new; }; printf 'status=%s\\n' \"$?\"; probe_z", "status=0\nold", "probe_z: readonly function\n", 0)]
    [InlineData("probe_f(){ :; }; declare -Fap probe_f; printf 'status=%s\\n' \"$?\"; declare -FAp probe_f; printf 'status=%s\\n' \"$?\"; declare -Fnp probe_f; printf 'status=%s\\n' \"$?\"", "declare -f probe_f\nstatus=0\ndeclare -f probe_f\nstatus=0\ndeclare -f probe_f\nstatus=0\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -F +a probe_f; printf 'status=%s\\n' \"$?\"; declare -F +n probe_f; printf 'status=%s\\n' \"$?\"", "status=0\nstatus=0\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -Fa +a probe_f; printf 'status=%s\\n' \"$?\"; declare -FnaA probe_f; printf 'status=%s\\n' \"$?\"", "status=1\nstatus=1\n", "declare: -a: invalid option\ndeclare: -n: invalid option\n", 0)]
    [InlineData("probe_f(){ :; }; f(){ local x=value; local -Fp; local -Fp probe_f; }; f", "declare -- x\ndeclare -f probe_f\n", "", 0)]
    [InlineData("probe_f(){ :; }; readonly -f probe_f; declare -fx +r probe_f; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "status=1\ndeclare -fr probe_f\n", "declare: probe_f: readonly function\n", 0)]
    [InlineData("probe_f(){ :; }; probe_g(){ :; }; readonly -f probe_f; declare -fx +r probe_f probe_g; printf 'status=%s\\n' \"$?\"; declare -F", "status=1\ndeclare -fr probe_f\ndeclare -fx probe_g\n", "declare: probe_f: readonly function\n", 0)]
    [InlineData("f(){ local -F; local -f; }; f", "", "", 0)]
    [InlineData("probe_f(){ :; }; declare -f +f probe_f; printf 'status=%s\\n' \"$?\"; declare -F", "status=0\ndeclare -f probe_f\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -F +F probe_f; declare -F +Fp probe_f; declare -p +F probe_f; printf 'status=%s\\n' \"$?\"", "declare -f probe_f\nstatus=1\n", "declare: probe_f: not found\n", 0)]
    [InlineData("probe_f(){ :; }; count=0; declare -F \"probe_$((count+=1))\" probe_f; printf 'count=%s status=%s\\n' \"$count\" \"$?\"", "probe_f\ncount=1 status=1\n", "", 0)]
    [InlineData("probe_f(){ :; }; probe_f=old; probe_f=temp export -f probe_f; declare -p probe_f; declare -Fp probe_f", "declare -- probe_f=\"old\"\ndeclare -fx probe_f\n", "", 0)]
    [InlineData("probe_f(){ :; }; eval 'readonly -f probe_f'; declare -Fp probe_f", "declare -fr probe_f\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -Fa; printf 'status=%s\\n' \"$?\"; declare -Fnp; printf 'status=%s\\n' \"$?\"", "status=0\nstatus=0\n", "", 0)]
    [InlineData("probe_f(){ :; }; export -af probe_f; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "status=0\ndeclare -fx probe_f\n", "", 0)]
    [InlineData("probe_f(){ :; }; export -afp probe_f; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "status=0\ndeclare -fx probe_f\n", "", 0)]
    [InlineData("f(){ local x=value; local -Fa; printf 'status=%s\\n' \"$?\"; }; f", "declare -- x\nstatus=0\n", "", 0)]
    [InlineData("probe_f(){ :; }; readonly -f probe_f; declare -Fp +r probe_f; printf 'status=%s\\n' \"$?\"", "declare -fr probe_f\nstatus=0\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -F probe_a=(x); printf 'status=%s\\n' \"$?\"; declare -p probe_a; declare -F", "status=1\ndeclare -a probe_a=([0]=\"x\")\ndeclare -f probe_f\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -f missing probe_f absent; printf 'status=%s\\n' \"$?\"", "probe_f () \n{ \n    :\n}\nstatus=1\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -F missing probe_f absent; printf 'status=%s\\n' \"$?\"", "probe_f\nstatus=1\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -fp missing probe_f absent; printf 'status=%s\\n' \"$?\"", "probe_f () \n{ \n    :\n}\nstatus=1\n", "declare: missing: not found\ndeclare: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -Fp missing probe_f absent; printf 'status=%s\\n' \"$?\"", "declare -f probe_f\nstatus=1\n", "declare: missing: not found\ndeclare: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -pF missing probe_f absent; printf 'status=%s\\n' \"$?\"", "declare -f probe_f\nstatus=1\n", "declare: missing: not found\ndeclare: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -f +p missing probe_f absent; printf 'status=%s\\n' \"$?\"", "probe_f () \n{ \n    :\n}\nstatus=1\n", "declare: missing: not found\ndeclare: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -F +p missing probe_f absent; printf 'status=%s\\n' \"$?\"", "declare -f probe_f\nstatus=1\n", "declare: missing: not found\ndeclare: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare +Fp missing probe_f absent; printf 'status=%s\\n' \"$?\"", "status=1\n", "declare: missing: not found\ndeclare: probe_f: not found\ndeclare: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; typeset -Fp missing probe_f absent; printf 'status=%s\\n' \"$?\"", "declare -f probe_f\nstatus=1\n", "typeset: missing: not found\ntypeset: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -Fxp missing probe_f absent; printf 'status=%s\\n' \"$?\"", "declare -f probe_f\nstatus=1\n", "declare: missing: not found\ndeclare: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -Fap missing probe_f absent; printf 'status=%s\\n' \"$?\"", "declare -f probe_f\nstatus=1\n", "declare: missing: not found\ndeclare: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; export -fp missing probe_f absent; printf 'status=%s\\n' \"$?\"", "status=1\n", "export: missing: not a function\nexport: absent: not a function\n", 0)]
    [InlineData("probe_f(){ :; }; readonly -fp missing probe_f absent; printf 'status=%s\\n' \"$?\"", "status=1\n", "readonly: missing: not a function\nreadonly: absent: not a function\n", 0)]
    [InlineData("probe_f(){ :; }; caller(){ local -fp missing probe_f absent; printf 'status=%s\\n' \"$?\"; }; caller", "probe_f () \n{ \n    :\n}\nstatus=1\n", "local: missing: not found\nlocal: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; caller(){ local -Fp missing probe_f absent; printf 'status=%s\\n' \"$?\"; }; caller", "declare -f probe_f\nstatus=1\n", "local: missing: not found\nlocal: absent: not found\n", 0)]
    [InlineData("probe_f(){ :; }; caller(){ local -F missing probe_f absent; printf 'status=%s\\n' \"$?\"; }; caller", "probe_f\nstatus=1\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -Fp ''; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "status=1\ndeclare -f probe_f\n", "declare: : not found\n", 0)]
    [InlineData("probe_f(){ :; }; probe_var=value; declare -Fp probe_var; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "status=1\ndeclare -f probe_f\n", "declare: probe_var: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -Fp 'probe_f=value' probe_f; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "declare -f probe_f\nstatus=1\ndeclare -f probe_f\n", "declare: probe_f=value: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -fp 'probe_f=value' probe_f; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "probe_f () \n{ \n    :\n}\nstatus=1\ndeclare -f probe_f\n", "declare: probe_f=value: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -fp ''; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "status=1\ndeclare -f probe_f\n", "declare: : not found\n", 0)]
    [InlineData("probe_f(){ :; }; caller(){ local -Fp 'probe_f=value' probe_f; }; caller; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "declare -f probe_f\nstatus=1\ndeclare -f probe_f\n", "local: probe_f=value: not found\n", 0)]
    [InlineData("probe_f(){ :; }; declare -fx missing probe_f; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "status=1\ndeclare -fx probe_f\n", "", 0)]
    [InlineData("probe_f(){ :; }; declare -frxp missing probe_f; printf 'status=%s\\n' \"$?\"; declare -Fp probe_f", "probe_f () \n{ \n    :\n}\nstatus=1\ndeclare -f probe_f\n", "declare: missing: not found\n", 0)]
    public async Task Evaluation_FunctionDeclarations_PreserveAttributesAndQueryModes(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script,
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }
}
