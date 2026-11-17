namespace Lokad.Bash.Tests;

public sealed partial class ShellBracketTests
{
    // Both pinned GNU 5.2 references agree on bytes and complete trees; qualification is retained in the S17 packet.
    public static IEnumerable<object[]> NativeCases()
    {
        foreach (var prepared in new[] { false, true })
        {
            yield return ["class-alnum-yes", "[[ 'A' == [[:alnum:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-alnum-no", "[[ '!' == [[:alnum:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-alpha-yes", "[[ 'a' == [[:alpha:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-alpha-no", "[[ '3' == [[:alpha:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-ascii-yes", "[[ '~' == [[:ascii:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-ascii-no", "[[ $'\\377' == [[:ascii:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-blank-yes", "[[ $'\\t' == [[:blank:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-blank-no", "[[ $'\\n' == [[:blank:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-cntrl-yes", "[[ $'\\n' == [[:cntrl:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-cntrl-no", "[[ ' ' == [[:cntrl:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-digit-yes", "[[ '5' == [[:digit:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-digit-no", "[[ 'd]' == [[:digit:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-graph-yes", "[[ '!' == [[:graph:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-graph-no", "[[ ' ' == [[:graph:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-lower-yes", "[[ 'z' == [[:lower:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-lower-no", "[[ 'Z' == [[:lower:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-print-yes", "[[ ' ' == [[:print:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-print-no", "[[ $'\\n' == [[:print:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-punct-yes", "[[ '.' == [[:punct:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-punct-no", "[[ 'a' == [[:punct:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-space-yes", "[[ $'\\r' == [[:space:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-space-no", "[[ 'x' == [[:space:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-upper-yes", "[[ 'A' == [[:upper:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-upper-no", "[[ 'a' == [[:upper:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-word-yes", "[[ '_' == [[:word:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-word-no", "[[ '-' == [[:word:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["class-xdigit-yes", "[[ 'F' == [[:xdigit:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["class-xdigit-no", "[[ 'g' == [[:xdigit:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["conditional-false-direct", "[[ 'd]' == [[:digit:]] ]] && : >effect; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["conditional-false-function", "f(){ [[ 'd]' == [[:digit:]] ]] && : >effect; }; f; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["conditional-false-eval", "eval '[[ '\\''d]'\\'' == [[:digit:]] ]] && : >effect'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["conditional-false-source", ". unit; printf tail", "7461696C", "{\"unit\":\"[[ 'd]' == [[:digit:]] ]] && : >effect\\n\"}", "[]", "{\"unit\":\"File:5B5B2027645D27203D3D205B5B3A64696769743A5D5D205D5D202626203A203E6566666563740A\"}", prepared];
            yield return ["conditional-false-modern", "v=$([[ 'd]' == [[:digit:]] ]] && : >effect); printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["conditional-false-export", "f(){ [[ 'd]' == [[:digit:]] ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["conditional-false-skipped", "false && { [[ 'd]' == [[:digit:]] ]] && : >effect; }; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["conditional-true-direct", "[[ 5 == [[:digit:]] ]] && : >effect; printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["conditional-true-function", "f(){ [[ 5 == [[:digit:]] ]] && : >effect; }; f; printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["conditional-true-eval", "eval '[[ 5 == [[:digit:]] ]] && : >effect'; printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["conditional-true-source", ". unit; printf tail", "7461696C", "{\"unit\":\"[[ 5 == [[:digit:]] ]] && : >effect\\n\"}", "[]", "{\"unit\":\"File:5B5B2035203D3D205B5B3A64696769743A5D5D205D5D202626203A203E6566666563740A\",\"effect\":\"File:\"}", prepared];
            yield return ["conditional-true-modern", "v=$([[ 5 == [[:digit:]] ]] && : >effect); printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["conditional-true-export", "f(){ [[ 5 == [[:digit:]] ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["conditional-true-skipped", "false && { [[ 5 == [[:digit:]] ]] && : >effect; }; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["case-false-direct", "case 'd]' in [[:digit:]]) : >effect;; esac; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["case-false-function", "f(){ case 'd]' in [[:digit:]]) : >effect;; esac; }; f; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["case-false-eval", "eval 'case '\\''d]'\\'' in [[:digit:]]) : >effect;; esac'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["case-false-source", ". unit; printf tail", "7461696C", "{\"unit\":\"case 'd]' in [[:digit:]]) : >effect;; esac\\n\"}", "[]", "{\"unit\":\"File:636173652027645D2720696E205B5B3A64696769743A5D5D29203A203E6566666563743B3B20657361630A\"}", prepared];
            yield return ["case-false-modern", "v=$(case 'd]' in [[:digit:]]) : >effect;; esac); printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["case-false-export", "f(){ case 'd]' in [[:digit:]]) : >effect;; esac; }; export -f f; bash -c 'f'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["case-false-skipped", "false && { case 'd]' in [[:digit:]]) : >effect;; esac; }; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["case-true-direct", "case 5 in [[:digit:]]) : >effect;; esac; printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["case-true-function", "f(){ case 5 in [[:digit:]]) : >effect;; esac; }; f; printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["case-true-eval", "eval 'case 5 in [[:digit:]]) : >effect;; esac'; printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["case-true-source", ". unit; printf tail", "7461696C", "{\"unit\":\"case 5 in [[:digit:]]) : >effect;; esac\\n\"}", "[]", "{\"unit\":\"File:63617365203520696E205B5B3A64696769743A5D5D29203A203E6566666563743B3B20657361630A\",\"effect\":\"File:\"}", prepared];
            yield return ["case-true-modern", "v=$(case 5 in [[:digit:]]) : >effect;; esac); printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["case-true-export", "f(){ case 5 in [[:digit:]]) : >effect;; esac; }; export -f f; bash -c 'f'; printf tail", "7461696C", "{}", "[]", "{\"effect\":\"File:\"}", prepared];
            yield return ["case-true-skipped", "false && { case 5 in [[:digit:]]) : >effect;; esac; }; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["prefix-false-direct", "x='d]'; [[ ${x#[[:digit:]]} == '' ]] && : >effect; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["prefix-false-function", "f(){ x='d]'; [[ ${x#[[:digit:]]} == '' ]] && : >effect; }; f; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["prefix-false-eval", "eval 'x='\\''d]'\\''; [[ ${x#[[:digit:]]} == '\\'''\\'' ]] && : >effect'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["prefix-false-source", ". unit; printf tail", "7461696C", "{\"unit\":\"x='d]'; [[ ${x#[[:digit:]]} == '' ]] && : >effect\\n\"}", "[]", "{\"unit\":\"File:783D27645D273B205B5B20247B78235B5B3A64696769743A5D5D7D203D3D202727205D5D202626203A203E6566666563740A\"}", prepared];
            yield return ["prefix-false-modern", "v=$(x='d]'; [[ ${x#[[:digit:]]} == '' ]] && : >effect); printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["prefix-false-export", "f(){ x='d]'; [[ ${x#[[:digit:]]} == '' ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["prefix-false-skipped", "false && { x='d]'; [[ ${x#[[:digit:]]} == '' ]] && : >effect; }; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["suffix-false-direct", "x='d]'; [[ ${x%[[:digit:]]} == '' ]] && : >effect; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["suffix-false-function", "f(){ x='d]'; [[ ${x%[[:digit:]]} == '' ]] && : >effect; }; f; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["suffix-false-eval", "eval 'x='\\''d]'\\''; [[ ${x%[[:digit:]]} == '\\'''\\'' ]] && : >effect'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["suffix-false-source", ". unit; printf tail", "7461696C", "{\"unit\":\"x='d]'; [[ ${x%[[:digit:]]} == '' ]] && : >effect\\n\"}", "[]", "{\"unit\":\"File:783D27645D273B205B5B20247B78255B5B3A64696769743A5D5D7D203D3D202727205D5D202626203A203E6566666563740A\"}", prepared];
            yield return ["suffix-false-modern", "v=$(x='d]'; [[ ${x%[[:digit:]]} == '' ]] && : >effect); printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["suffix-false-export", "f(){ x='d]'; [[ ${x%[[:digit:]]} == '' ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["suffix-false-skipped", "false && { x='d]'; [[ ${x%[[:digit:]]} == '' ]] && : >effect; }; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["replace-false-direct", "x='d]'; [[ ${x/[[:digit:]]/} == '' ]] && : >effect; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["replace-false-function", "f(){ x='d]'; [[ ${x/[[:digit:]]/} == '' ]] && : >effect; }; f; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["replace-false-eval", "eval 'x='\\''d]'\\''; [[ ${x/[[:digit:]]/} == '\\'''\\'' ]] && : >effect'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["replace-false-source", ". unit; printf tail", "7461696C", "{\"unit\":\"x='d]'; [[ ${x/[[:digit:]]/} == '' ]] && : >effect\\n\"}", "[]", "{\"unit\":\"File:783D27645D273B205B5B20247B782F5B5B3A64696769743A5D5D2F7D203D3D202727205D5D202626203A203E6566666563740A\"}", prepared];
            yield return ["replace-false-modern", "v=$(x='d]'; [[ ${x/[[:digit:]]/} == '' ]] && : >effect); printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["replace-false-export", "f(){ x='d]'; [[ ${x/[[:digit:]]/} == '' ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["replace-false-skipped", "false && { x='d]'; [[ ${x/[[:digit:]]/} == '' ]] && : >effect; }; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["global-false-direct", "x='d]'; [[ ${x//[[:digit:]]/} == '' ]] && : >effect; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["global-false-function", "f(){ x='d]'; [[ ${x//[[:digit:]]/} == '' ]] && : >effect; }; f; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["global-false-eval", "eval 'x='\\''d]'\\''; [[ ${x//[[:digit:]]/} == '\\'''\\'' ]] && : >effect'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["global-false-source", ". unit; printf tail", "7461696C", "{\"unit\":\"x='d]'; [[ ${x//[[:digit:]]/} == '' ]] && : >effect\\n\"}", "[]", "{\"unit\":\"File:783D27645D273B205B5B20247B782F2F5B5B3A64696769743A5D5D2F7D203D3D202727205D5D202626203A203E6566666563740A\"}", prepared];
            yield return ["global-false-modern", "v=$(x='d]'; [[ ${x//[[:digit:]]/} == '' ]] && : >effect); printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["global-false-export", "f(){ x='d]'; [[ ${x//[[:digit:]]/} == '' ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["global-false-skipped", "false && { x='d]'; [[ ${x//[[:digit:]]/} == '' ]] && : >effect; }; printf tail", "7461696C", "{}", "[]", "{}", prepared];
            yield return ["context-union", "[[ 'a' == [[:digit:][:alpha:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["context-negated", "[[ '5' == [![:digit:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["context-unknown", "[[ 'a' == [[:unknown:]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["context-unknown-negated", "[[ 'a' == [![:unknown:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["context-collating", "[[ 'a' == [[.a.]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["context-equivalent", "[[ 'a' == [[=a=]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["context-range", "[[ 'b' == [[.a.]-[.c.]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["context-unknown-symbol", "[[ 'ch' == [[.ch.]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["context-quoted-name", "[[ '5' == [[:di\"g\"it:]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["context-quoted-symbol", "[[ 'a' == [[.\"a\".]] ]]; printf '<%s>' \"$?\"", "3C313E", "{}", "[]", "{}", prepared];
            yield return ["context-quoted-equivalence", "[[ 'a]' == [[=\"a\"=]] ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["parameter-directions", "x='5d]'; printf '<%s>' \"${x#[[:digit:]]}\" \"${x%[[:digit:]]}\" \"${x##[[:digit:]]*}\" \"${x%%*[[:digit:]]}\"", "3C645D3E3C35645D3E3C3E3C35645D3E", "{}", "[]", "{}", prepared];
            yield return ["parameter-suffix-and-global", "x='d]5'; printf '<%s>' \"${x%[[:digit:]]}\" \"${x//[[:digit:]]/X}\"", "3C645D3E3C645D583E", "{}", "[]", "{}", prepared];
            yield return ["parameter-array", "a=(5 'd]' a5); printf '<%s>' \"${a[@]#[[:digit:]]}\"", "3C3E3C645D3E3C61353E", "{}", "[]", "{}", prepared];
            yield return ["quoted-pattern", "[[ '[[:digit:]]' == '[[:digit:]]' ]]; printf '<%s>' \"$?\"", "3C303E", "{}", "[]", "{}", prepared];
            yield return ["path-matches", "printf '<%s>' [[:digit:]]", "3C353E", "{\"5\":\"\",\"d]\":\"\"}", "[]", "{\"5\":\"File:\",\"d]\":\"File:\"}", prepared];
            yield return ["path-false-effect", "for f in [[:digit:]]; do [[ $f == 'd]' ]] && : >effect; done; printf tail", "7461696C", "{\"5\":\"\",\"d]\":\"\"}", "[]", "{\"5\":\"File:\",\"d]\":\"File:\"}", prepared];
            yield return ["path-quoted", "printf '<%s>' '[[:digit:]]'", "3C5B5B3A64696769743A5D5D3E", "{\"5\":\"\",\"d]\":\"\"}", "[]", "{\"5\":\"File:\",\"d]\":\"File:\"}", prepared];
            yield return ["path-no-match", "printf '<%s>' [[:digit:]]", "3C5B5B3A64696769743A5D5D3E", "{\"d]\":\"\"}", "[]", "{\"d]\":\"File:\"}", prepared];
            yield return ["path-collating", "printf '<%s>' [[.a.]]", "3C613E", "{\"a\":\"\",\"a]\":\"\"}", "[]", "{\"a\":\"File:\",\"a]\":\"File:\"}", prepared];
            yield return ["path-equivalence", "printf '<%s>' [[=a=]]", "3C613E", "{\"a\":\"\",\"a]\":\"\"}", "[]", "{\"a\":\"File:\",\"a]\":\"File:\"}", prepared];
            yield return ["path-directory-kind", "printf '<%s>' [[:digit:]]", "3C353E3C373E", "{\"5\":\"\"}", "[\"7\"]", "{\"5\":\"File:\",\"7\":\"Directory:\"}", prepared];
        }
    }
}
