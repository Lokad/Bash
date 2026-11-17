namespace Lokad.Bash.Tests;

public sealed partial class ConditionalLexingTests
{
    // Both pinned GNU 5.2 references agree; source, identities and raw complete trees are frozen in the R49 packet.
    public static IEnumerable<object[]> NativeCases()
    {
        foreach (var prepared in new[] { false, true })
        {
            yield return ["comment-before-left", "[[ # ' $( ]] && inert\nx == x ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-before-operator", "[[ x # ' $( ]] && inert\n== x ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-before-right", "[[ x == # ' $( ]] && inert\nx ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-trailing", "[[ x == x # ' $( ]] && inert\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-before-unary-operand", "[[ -n # ' $( ]] && inert\nx ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-after-negation", "[[ ! # ' $( ]] && inert\n'' ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-after-open", "[[ (# ' $( ]] && inert\nx == x) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-before-close", "[[ (x == x # ' $( ]] && inert\n) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-before-and", "[[ x # ' $( ]] && inert\n&& x ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-after-and", "[[ x && # ' $( ]] && inert\nx ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-before-or", "[[ '' # ' $( ]] && inert\n|| x ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-after-or", "[[ '' || # ' $( ]] && inert\nx ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-regex-operand", "[[ x =~ # ' $( ]] && inert\nx ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-two-comments", "[[ x # one\n== # two\nx ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-backslash-in-comment", "[[ x == x # inert \\\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-continued-comment-start", "[[ x == x \\\n# comment\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-continued-operator", "[[ x =\\\n= x # end\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-continued-and", "[[ x &\\\n& x # end\n ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-continued-or", "[[ '' |\\\n| x # end\n ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-continued-unary", "[[ -\\\nn x # end\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-continued-negation", "[[ !\\\n '' # end\n ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-comment-then-continuation", "[[ x == # inert\n\\\nx ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["comment-physical-cr-comment", "[[ x == x # inert\r ]] ' $(\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-direct", "[[ ']]' =~ (]]) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-function", "f(){ [[ ']]' =~ (]]) ]] && : >effect; }; f; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-source", ". unit; printf tail", 0, "7461696C", false, "{\"unit\":\"[[ ']]' =~ (]]) ]] && : >effect\\n\"}", "{\"effect\":\"File:\",\"unit\":\"File:5B5B20275D5D27203D7E20285D5D29205D5D202626203A203E6566666563740A\"}", false, prepared];
            yield return ["regex-close-eval", "eval '[[ '\\'']]'\\'' =~ (]]) ]] && : >effect'; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-modern", "value=$([[ ']]' =~ (]]) ]] && : >effect); printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-legacy", "value=`[[ ']]' =~ (]]) ]] && : >effect`; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-subshell", "([[ ']]' =~ (]]) ]] && : >effect); printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-replay", "f(){ [[ ']]' =~ (]]) ]] && : >effect; }; definition=$(declare -f f); unset -f f; eval \"$definition\"; f; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-export", "f(){ [[ ']]' =~ (]]) ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-close-skipped", "false && [[ ']]' =~ (]]) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{}", false, prepared];
            yield return ["regex-after-alternation-direct", "[[ ']]' =~ (x|]]) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-after-alternation-function", "f(){ [[ ']]' =~ (x|]]) ]] && : >effect; }; f; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-after-alternation-source", ". unit; printf tail", 0, "7461696C", false, "{\"unit\":\"[[ ']]' =~ (x|]]) ]] && : >effect\\n\"}", "{\"effect\":\"File:\",\"unit\":\"File:5B5B20275D5D27203D7E2028787C5D5D29205D5D202626203A203E6566666563740A\"}", false, prepared];
            yield return ["regex-after-alternation-eval", "eval '[[ '\\'']]'\\'' =~ (x|]]) ]] && : >effect'; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-after-alternation-modern", "value=$([[ ']]' =~ (x|]]) ]] && : >effect); printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-after-alternation-legacy", "value=`[[ ']]' =~ (x|]]) ]] && : >effect`; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-after-alternation-subshell", "([[ ']]' =~ (x|]]) ]] && : >effect); printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-after-alternation-replay", "f(){ [[ ']]' =~ (x|]]) ]] && : >effect; }; definition=$(declare -f f); unset -f f; eval \"$definition\"; f; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-after-alternation-export", "f(){ [[ ']]' =~ (x|]]) ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["regex-after-alternation-skipped", "false && [[ ']]' =~ (x|]]) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{}", false, prepared];
            yield return ["comment-direct", "[[ x == x # inert ]] ' $(\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-function", "f(){ [[ x == x # inert ]] ' $(\n ]] && : >effect; }; f; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-source", ". unit; printf tail", 0, "7461696C", false, "{\"unit\":\"[[ x == x # inert ]] ' $(\\n ]] && : >effect\\n\"}", "{\"effect\":\"File:\",\"unit\":\"File:5B5B2078203D3D2078202320696E657274205D5D20272024280A205D5D202626203A203E6566666563740A\"}", false, prepared];
            yield return ["comment-eval", "eval '[[ x == x # inert ]] '\\'' $(\n ]] && : >effect'; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-modern", "value=$([[ x == x # inert ]] ' $(\n ]] && : >effect); printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-legacy", "value=`[[ x == x # inert ]] ' $(\n ]] && : >effect`; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-subshell", "([[ x == x # inert ]] ' $(\n ]] && : >effect); printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-replay", "f(){ [[ x == x # inert ]] ' $(\n ]] && : >effect; }; definition=$(declare -f f); unset -f f; eval \"$definition\"; f; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-export", "f(){ [[ x == x # inert ]] ' $(\n ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["comment-skipped", "false && [[ x == x # inert ]] ' $(\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{}", false, prepared];
            yield return ["skipped-expansion-direct", "[[ x == x || $( : >forbidden ) # inert\n ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["skipped-expansion-function", "f(){ [[ x == x || $( : >forbidden ) # inert\n ]] && : >effect; }; f; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["skipped-expansion-source", ". unit; printf tail", 0, "7461696C", true, "{\"unit\":\"[[ x == x || $( : >forbidden ) # inert\\n ]] && : >effect\\n\"}", "{\"unit\":\"File:5B5B2078203D3D2078207C7C202428203A203E666F7262696464656E2029202320696E6572740A205D5D202626203A203E6566666563740A\"}", false, prepared];
            yield return ["skipped-expansion-eval", "eval '[[ x == x || $( : >forbidden ) # inert\n ]] && : >effect'; printf tail", 0, "7461696C", true, "{}", "{}", false, prepared];
            yield return ["skipped-expansion-modern", "value=$([[ x == x || $( : >forbidden ) # inert\n ]] && : >effect); printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["skipped-expansion-legacy", "value=`[[ x == x || $( : >forbidden ) # inert\n ]] && : >effect`; printf tail", 0, "7461696C", true, "{}", "{}", false, prepared];
            yield return ["skipped-expansion-subshell", "([[ x == x || $( : >forbidden ) # inert\n ]] && : >effect); printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["skipped-expansion-replay", "f(){ [[ x == x || $( : >forbidden ) # inert\n ]] && : >effect; }; definition=$(declare -f f); unset -f f; eval \"$definition\"; f; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["skipped-expansion-export", "f(){ [[ x == x || $( : >forbidden ) # inert\n ]] && : >effect; }; export -f f; bash -c 'f'; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["skipped-expansion-skipped", "false && [[ x == x || $( : >forbidden ) # inert\n ]] && : >effect; printf tail", 2, "", true, "{}", "{}", true, prepared];
            yield return ["control-quoted", "[[ '#' == '#' ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-escaped", "[[ \\# == '#' ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-embedded", "[[ x# == x# ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-regex-group", "[[ '#' =~ (#) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-regex-class", "[[ '#' =~ [#] ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-quoted-continued", "[[ 'x\\\ny' == 'x\\\ny' ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-double-continued", "[[ \"x\\\ny\" == xy ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-ansi-opener", "[[ $\\\n'x' == x ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-single-continuation", "[[ $'x\\\ny' == $'x\\\ny' ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-regex-negative", "[[ wrong =~ (]]) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{}", false, prepared];
            yield return ["control-false-and", "[[ '' && $( : >forbidden ) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{}", false, prepared];
            yield return ["control-regex-named-class", "[[ 5 =~ [[:digit:]] ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["control-regex-leading-bracket", "[[ ']' =~ ([]]) ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["nested-continuation", "[[ $(printf 'x\\\ny') == $'x\\\ny' ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["nested-literal-comment", "[[ $(printf '#') == '#' # inert\n ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["heredoc", "[[ $(cat <<END\n]] # ' literal\nEND\n) == \"]] # ' literal\" ]] && : >effect; printf tail", 0, "7461696C", false, "{}", "{\"effect\":\"File:\"}", false, prepared];
            yield return ["newline-0", "[[ x\n ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-1", "[[ x\n&& x ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-2", "[[ x\n== x ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-3", "[[ x ==\nx ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-4", "[[ -n\nx ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-5", "[[ x == x\n ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-6", "[[ -n x\n ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-7", "[[ !\n x ]]; printf \"<%s>\" \"$?\"", 0, "3C313E", false, "{}", "{}", false, prepared];
            yield return ["newline-8", "[[ ! x\n ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-9", "[[ (\nx) ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-10", "[[ (x\n) ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-11", "[[ x &&\nx ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-12", "[[ x ||\nx ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-13", "[[ x == x\n&& x ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-14", "[[ x && x == x\n ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-15", "[[ x &\\\n& x == x ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-16", "[[ '' |\\\n| x == x ]]; printf \"<%s>\" \"$?\"", 0, "3C303E", false, "{}", "{}", false, prepared];
            yield return ["newline-17", "[[ !\\\n x ]]; printf \"<%s>\" \"$?\"", 0, "3C313E", false, "{}", "{}", false, prepared];
            yield return ["newline-18", "[[ x ==\n# comment\nx ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-19", "[[ x\n# comment\n ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
            yield return ["newline-20", "[[ x\n\n ]]; printf \"<%s>\" \"$?\"", 2, "", true, "{}", "{}", true, prepared];
        }
    }
}
