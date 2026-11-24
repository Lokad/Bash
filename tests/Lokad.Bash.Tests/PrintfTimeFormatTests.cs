using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Formatting;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class PrintfTimeFormatTests
{
    public static IEnumerable<object[]> Cases()
    {
        // Linux strftime preserves unknown directive bytes; MSYS spelling differences remain qualified.
        object[][] samples =
        [
            ["literal-byte", "printf $'L\\377R' 0; printf '|%s|tail' \"$?\"\n", "4CFF527C307C7461696C", false],
            ["time-byte", "printf $'%(L\\377R)T' 0; printf '|%s|tail' \"$?\"\n", "4CFF527C307C7461696C", false],
            ["time-byte-date", "printf $'%(\\377%Y\\376)T' 0; printf '|%s|tail' \"$?\"\n", "FF31393730FE7C307C7461696C", false],
            ["time-byte-directive", "printf $'%(%\\377)T' 0; printf '|%s|tail' \"$?\"\n", "25FF7C307C7461696C", false],
            ["time-byte-truncated", "printf $'%.0(\\377)T' 0; printf '|%s|tail' \"$?\"\n", "7C307C7461696C", false],
            ["time-byte-width", "printf $'%5(\\377)T' 0; printf '|%s|tail' \"$?\"\n", "20202020FF7C307C7461696C", false],
            ["time-empty", "printf $'%()T' 0; printf '|%s|tail' \"$?\"\n", "30303A30303A30307C307C7461696C", false],
            ["time-percent", "printf $'%(%)T' 0; printf '|%s|tail' \"$?\"\n", "257C307C7461696C", false],
            ["time-parenthesis", "printf $'%(a(b)c)T' 0; printf '|%s|tail' \"$?\"\n", "61286229637C307C7461696C", false],
            ["time-ascii", "printf $'%(abc%Y)T' 0; printf '|%s|tail' \"$?\"\n", "616263313937307C307C7461696C", false],
            ["time-utf8", "printf $'%(\\303\\251%Y)T' 0; printf '|%s|tail' \"$?\"\n", "C3A9313937307C307C7461696C", false],
            ["time-byte-closed", "printf $'%(\\377)T' 0 2>&-; printf '|%s|tail' \"$?\"\n", "FF7C307C7461696C", false],
            ["time-byte-redirected", "printf $'%(\\377)T' 0 2>out; printf '|%s|tail' \"$?\"\n", "FF7C307C7461696C", true],
            ["time-byte-v", "v=old; printf -v v $'pre%(\\377)Tpost' 0; printf '|%s|<%s>|tail' \"$?\" \"$v\"\n", "7C307C3C707265FF706F73743E7C7461696C", false],
            ["time-byte-unreached", "false && printf $'%(\\377)T' 0; printf tail\n", "7461696C", false],
            ["time-byte-after-count", "n=old; printf $'a%n%(\\377)T' n 0; printf '|%s|<%s>|tail' \"$?\" \"$n\"\n", "61FF7C307C3C313E7C7461696C", false],
            ["time-width-ascii", "printf -v v $'%(%1Y)T' 0; printf '%s:<%s>' \"$?\" \"$v\"\n", "303A3C313937303E", false],
            ["time-width-arabic", "printf -v v $'%(%\\331\\241Y)T' 0; printf '%s:<%s>' \"$?\" \"$v\"\n", "303A3C25D9A1593E", false],
            ["time-width-fullwidth", "printf -v v $'%(%\\357\\274\\221Y)T' 0; printf '%s:<%s>' \"$?\" \"$v\"\n", "303A3C25EFBC91593E", false],
            ["time-width-arabic-two", "printf -v v $'%(%\\331\\241\\331\\241Y)T' 0; printf '%s:<%s>' \"$?\" \"$v\"\n", "303A3C25D9A1D9A1593E", false],
            ["time-high-bytes", "printf $'%(\\200\\201\\202\\203\\204\\205\\206\\207\\210\\211\\212\\213\\214\\215\\216\\217\\220\\221\\222\\223\\224\\225\\226\\227\\230\\231\\232\\233\\234\\235\\236\\237\\240\\241\\242\\243\\244\\245\\246\\247\\250\\251\\252\\253\\254\\255\\256\\257\\260\\261\\262\\263\\264\\265\\266\\267\\270\\271\\272\\273\\274\\275\\276\\277\\300\\301\\302\\303\\304\\305\\306\\307\\310\\311\\312\\313\\314\\315\\316\\317\\320\\321\\322\\323\\324\\325\\326\\327\\330\\331\\332\\333\\334\\335\\336\\337\\340\\341\\342\\343\\344\\345\\346\\347\\350\\351\\352\\353\\354\\355\\356\\357\\360\\361\\362\\363\\364\\365\\366\\367\\370\\371\\372\\373\\374\\375\\376\\377)T' 0; printf '|%s|tail' \"$?\"\n", "7C307C7461696C", false],
            ["time-utf8-split", "printf $'<%5.1(\\303\\251)T>' 0; printf '|%s|tail' \"$?\"\n", "3C20202020C33E7C307C7461696C", false],
            ["time-utf8-symbols", "printf $'%(\\303\\251\\360\\237\\231\\202%Y)T' 0; printf '|%s|tail' \"$?\"\n", "C3A9F09F9982313937307C307C7461696C", false],
            ["time-unknown-modifier-byte", "printf $'%(%^\\377)T' 0; printf '|%s|tail' \"$?\"\n", "255EFF7C307C7461696C", false],
            ["time-count-and-byte-v", "n=old; printf -v v $'x%n%(\\377%Y)T%n' before 0 after; printf '<%s><%s><%s><%s>' \"$?\" \"$v\" \"$before\" \"$after\"\n", "3C303E3C78FF313937303E3C313E3C363E", false],
            ["time-byte-reused", "printf $'%(\\377%s)T;' 0 1; printf '|%s|tail' \"$?\"\n", "FF303BFF313B7C307C7461696C", false],
            ["time-extended-byte-precision", "printf $'<%9.6(\\377%Y)T>' 253402300800", "3C202020FF31303030303E", false],
            ["time-extended-reused", "printf '<%(%s)T>' -62135596801 -3 0 253402300799 253402300800",
                Convert.ToHexString("<-62135596801><-3><0><253402300799><253402300800>"u8), false],
            ["time-extended-count", "printf -v v 'a%n%(%s)T%n' n 253402300800 end; printf '<%s><%s><%s><%s>' \"$?\" \"$v\" \"$n\" \"$end\"",
                "3C303E3C613235333430323330303830303E3C313E3C31333E", false],
            ["time-extended-buffer-127", "printf '<%(" + new string('x', 122) + "%Y)T>' 253402300800",
                "3C" + string.Concat(Enumerable.Repeat("78", 122)) + "31303030303E", false],
            ["time-extended-buffer-128", "printf '<%(" + new string('x', 123) + "%Y)T>' 253402300800", "3C3E", false],
            ["time-width-unknown-controls", "printf '<%(%3Y)T><%(%J)T><%(%_J)T>' 0 0 0; printf '|%s|tail' \"$?\"\n", "3C313937303E3C254A3E3C255F4A3E7C307C7461696C", false],
            ["time-modifier-E", "printf '<%(%E%|%En|%Et|%Ep|%EP|%Er|%ER|%Es|%ET|%Ez|%EZ|%Eu)T>' 3661",
                Convert.ToHexString("<%|\n|\t|AM|am|01:01:01 AM|01:01|3661|01:01:01|+0000|UTC|4>"u8), false],
            ["time-modifier-O-shared", "printf '<%(%O%|%On|%Ot|%Op|%OP|%Or|%OR|%Os|%OT|%Oz|%OZ)T>' 3661",
                Convert.ToHexString("<%|\n|\t|AM|am|01:01:01 AM|01:01|3661|01:01:01|+0000|UTC>"u8), false],
            ["time-modifier-O-specific", "printf '<%(%Ok|%Ol|%Ob|%OB|%OC|%Og|%OG|%Oh|%Oj)T>' 0",
                Convert.ToHexString("< 0|12|Jan|January|19|70|1970|Jan|001>"u8), false],
            ["time-modifier-unknown", "printf '<%(%EH|%Ed|%EJ|%Oa|%OY|%OJ)T>' 0",
                Convert.ToHexString("<%EH|%Ed|%EJ|%Oa|%OY|%OJ>"u8), false],
            ["time-modifier-supported", "printf '<%(%Ec|%EY|%OH|%Od|%Ou|%Oy)T>' 0",
                Convert.ToHexString("<Thu Jan  1 00:00:00 1970|1970|00|01|4|70>"u8), false],
            ["time-modifier-count", "printf -v stamp 'x%(%ET|%Oj)T%n' 3661 count; printf '<%s><%s><%s>' \"$?\" \"$stamp\" \"$count\"",
                Convert.ToHexString("<0><x01:01:01|001><13>"u8), false],
            ["time-modifier-literal-bytes", "printf $'<%(\\377%ET%\\376%OJ)T>' 3661",
                "3CFF30313A30313A303125FE254F4A3E", false],
            ["time-modifier-outer-field", "printf '<%10.5(%ET|%Oj)T>' 3661",
                Convert.ToHexString("<     01:01>"u8), false],
            ["time-modifier-buffer-127", "printf '<%(" + new string('x', 119) + "%ET)T>' 0",
                Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("<" + new string('x', 119) + "00:00:00>")), false],
            ["time-modifier-buffer-128", "printf '<%(" + new string('x', 120) + "%ET)T>' 0",
                "3C3E", false],
            ["time-padding-zero", "printf '<%(%H|%_H|%-H|%0H|%_M|%_S|%_U|%_W|%_j)T>' 0",
                Convert.ToHexString("<00| 0|0|00| 0| 0| 0| 0|  1>"u8), false],
            ["time-padding-nonzero", "printf '<%(%H|%_H|%-H|%0H|%_M|%_S|%_U|%_W|%_j)T>' 3661",
                Convert.ToHexString("<01| 1|1|01| 1| 1| 0| 0|  1>"u8), false],
            ["time-padding-space-default", "printf '<%(%e|%_e|%-e|%0e|%k|%_k|%-k|%0k|%l|%_l|%-l|%0l)T>' 3661",
                Convert.ToHexString("< 1| 1|1|01| 1| 1|1|01| 1| 1|1|01>"u8), false],
            ["time-padding-numeric-width", "printf '<%(%_1H|%1H|%01H|%-1H|%_4H|%4H|%04H|%-4H|%_Oj|%-5Oj)T>' 0",
                Convert.ToHexString("< 0|00|00|0|   0|0000|0000|   0|  1|    1>"u8), false],
            ["time-padding-flag-precedence", "printf '<%(%0_H|%_0H|%_-H|%-_H|%-_5OH|%_0-5OH)T>' 0",
                Convert.ToHexString("< 0|00|0| 0|    0|    0>"u8), false],
            ["time-padding-year-suffix", "printf '<%(%_y|%-y|%-4y|%_g|%-g|%_V|%_G)T>' 946684800",
                Convert.ToHexString("< 0|0|   0|99|99|52|1999>"u8), false],
            ["time-padding-text-width", "printf '<%(%a|%_a|%0a|%-a|%-10a|%-5p|%-4t|%-3%)T>' 0",
                Convert.ToHexString("<Thu|Thu|Thu|Thu|       Thu|   AM|   \t|  %>"u8), false],
            ["time-padding-composite", "printf '<%(%_T|%-T|%-10T|%010T|%_R|%_X|%_D|%-x|%-Dr)T>' 0",
                Convert.ToHexString("<00:00:00|00:00:00|  00:00:00|0000:00:00|00:00|00:00:00|01/01/70|01/01/70|01/01/70r>"u8), false],
            ["time-padding-composite-modifier", "printf '<%(%_OT|%-ET|%_EX|%-10Ex|%_Or|%-10r|%012r)T>' 3661",
                Convert.ToHexString("<01:01:01|01:01:01|01:01:01|  01/01/70|01:01:01 AM|01:01:01 AM|001:01:01 AM>"u8), false],
            ["time-padding-outer-fields", "printf '<%6.2(%_4H)T><%-7.3(%-6a)T>' 0 0",
                Convert.ToHexString("<      ><       >"u8), false],
            ["time-padding-count", "printf -v stamp 'x%(%-6H|%_T)T%n' 0 count; printf '<%s><%s><%s>' \"$?\" \"$stamp\" \"$count\"",
                Convert.ToHexString("<0><x     0|00:00:00><16>"u8), false],
            ["time-padding-literal-bytes", "printf $'<%(\\377%_OH\\376%_T\\300)T>' 0",
                "3CFF2030FE30303A30303A3030C03E", false],
            ["time-padding-trim-127", "printf '<%(" + new string('x', 126) + "%-H)T>' 0",
                Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("<" + new string('x', 126) + "0>")), false],
            ["time-padding-trim-128", "printf '<%(" + new string('x', 127) + "%-H)T>' 0",
                "3C3E", false],
            ["time-padding-width-127", "printf '<%(" + new string('x', 122) + "%-5H)T>' 0",
                Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("<" + new string('x', 122) + "    0>")), false],
            ["time-padding-width-128", "printf '<%(" + new string('x', 123) + "%-5H)T>' 0",
                "3C3E", false],
            ["time-case-day-and-month", "printf '<%(%#a|%#A|%#b|%#B|%#h|%#Ob|%#OB|%#Oh|%#^a|%^#b)T>' 0",
                Convert.ToHexString("<THU|THURSDAY|JAN|JANUARY|JAN|JAN|JANUARY|JAN|THU|JAN>"u8), false],
            ["time-case-meridiem-and-zone", "printf '<%(%p|%#p|%^p|%^#p|%#^p|%^P|%#P|%^#P|%#^P|%#Z|%^#Z|%#^Z)T>' 54000",
                Convert.ToHexString("<PM|pm|PM|pm|pm|pm|pm|pm|pm|utc|utc|utc>"u8), false],
            ["time-case-composites", "printf '<%(%#c|%^c|%#r|%#Ec)T>' 0",
                Convert.ToHexString("<Thu Jan  1 00:00:00 1970|THU JAN  1 00:00:00 1970|12:00:00 AM|Thu Jan  1 00:00:00 1970>"u8), false],
            ["time-case-with-width", "printf '<%(%#8a|%#08p|%#08Z|%^08P|%-#8h|%#15r)T>' 0",
                Convert.ToHexString("<     THU|000000am|00000utc|000000am|     JAN|    12:00:00 AM>"u8), false],
            ["time-literal-directive-width", "printf '<%(%8J|%08J|%-8J|%^8q|%#8q|%08N|%^08+)T>' 0",
                Convert.ToHexString("<     %8J|0000%08J|    %-8J|    %^8Q|    %#8q|0000%08N|000%^08+>"u8), false],
            ["time-literal-directive-modifier", "printf '<%(%#Ea|%#Eb|%#Eh|%#Ej|%^Ej|%#8Eb|%^#08Oq)T>' 0",
                Convert.ToHexString("<%#Ea|%#EB|%#EH|%#Ej|%^EJ|   %#8EB|0%^#08OQ>"u8), false],
            ["time-literal-colons-and-following-directive", "printf '<%(%8:q|%^:q|%:8z|%8:%Y|%::%Y|%E:%Y)T>' 0",
                Convert.ToHexString("<     %8:q|%^:q|%:8z|     %8:1970|%::1970|%E:1970>"u8), false],
            ["time-incomplete-directive-width", "printf '<%(%^)T><%(%#)T><%(%08)T><%(%8E)T><%(%_8O)T>' 0 0 0 0 0",
                Convert.ToHexString("<%^><%#><00000%08><     %8E><    %_8O>"u8), false],
            ["time-literal-directive-opaque-bytes", "printf $'<%(\\377%^\\346%#\\360%_8\\376%08\\300)T>' 0",
                "3CFF255EE62523F020202020255F38FE30303030253038C03E", false],
            ["time-literal-directive-limit-127", "printf '<%(x%126J)T>' 0",
                Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("<x" + new string(' ', 121) + "%126J>")), false],
            ["time-literal-directive-limit-128", "printf '<%(xx%126J)T>' 0", "3C3E", false],
            ["time-case-and-literal-count", "printf -v v 'x%12.6(%^8q|%#p)T%n' 0 count; printf '<%s><%s><%s>' \"$?\" \"$v\" \"$count\"",
                Convert.ToHexString("<0><x          %^><13>"u8), false],
            ["time-buffer-literal-126", "printf '<%(xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx)T>' 0; printf '|%s|tail' \"$?\"\n", "3C7878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878783E7C307C7461696C", false],
            ["time-buffer-literal-127", "printf '<%(xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx)T>' 0; printf '|%s|tail' \"$?\"\n", "3C787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878787878783E7C307C7461696C", false],
            ["time-buffer-literal-128", "printf '<%(xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx)T>' 0; printf '|%s|tail' \"$?\"\n", "3C3E7C307C7461696C", false],
            ["time-buffer-literal-129", "printf '<%(xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx)T>' 0; printf '|%s|tail' \"$?\"\n", "3C3E7C307C7461696C", false],
            ["time-buffer-expanded", "printf '<%(%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y%Y)T>' 0; printf '|%s|tail' \"$?\"\n", "3C3E7C307C7461696C", false],
            ["time-buffer-inner-width", "printf '<%(%200Y)T>' 0; printf '|%s|tail' \"$?\"\n", "3C3E7C307C7461696C", false],
            ["time-buffer-outer-width", "printf '<%200(%Y)T>' 0; printf '|%s|tail' \"$?\"\n", "3C20202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020202020313937303E7C307C7461696C", false],
            ["time-buffer-count-v", "before=old; after=old; printf -v v 'x%n%(xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx)T%n' before 0 after; printf '<%s><%s><%s><%s>' \"$?\" \"$v\" \"$before\" \"$after\"\n", "3C303E3C783E3C313E3C313E", false],
            ["time-buffer-truncated", "printf '<%.1(xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx)T>;' 0; printf '|%s|tail' \"$?\"\n", "3C3E3B7C307C7461696C", false],
            ["time-high-byte-half-128", "printf $'%(\\200\\201\\202\\203\\204\\205\\206\\207\\210\\211\\212\\213\\214\\215\\216\\217\\220\\221\\222\\223\\224\\225\\226\\227\\230\\231\\232\\233\\234\\235\\236\\237\\240\\241\\242\\243\\244\\245\\246\\247\\250\\251\\252\\253\\254\\255\\256\\257\\260\\261\\262\\263\\264\\265\\266\\267\\270\\271\\272\\273\\274\\275\\276\\277)T' 0; printf '|%s|tail' \"$?\"\n", "808182838485868788898A8B8C8D8E8F909192939495969798999A9B9C9D9E9FA0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF7C307C7461696C", false],
            ["time-high-byte-half-192", "printf $'%(\\300\\301\\302\\303\\304\\305\\306\\307\\310\\311\\312\\313\\314\\315\\316\\317\\320\\321\\322\\323\\324\\325\\326\\327\\330\\331\\332\\333\\334\\335\\336\\337\\340\\341\\342\\343\\344\\345\\346\\347\\350\\351\\352\\353\\354\\355\\356\\357\\360\\361\\362\\363\\364\\365\\366\\367\\370\\371\\372\\373\\374\\375\\376\\377)T' 0; printf '|%s|tail' \"$?\"\n", "C0C1C2C3C4C5C6C7C8C9CACBCCCDCECFD0D1D2D3D4D5D6D7D8D9DADBDCDDDEDFE0E1E2E3E4E5E6E7E8E9EAEBECEDEEEFF0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF7C307C7461696C", false],
        ];
        foreach (var prepared in new[] { false, true })
        foreach (var sample in samples)
            yield return [prepared, ..sample];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public Task TimeFormatsPreserveBytesFieldsCountsAndStatus(
        bool prepared, string name, string source, string outputHex, bool redirected) =>
        AssertTimeFormatAsync(prepared, name, source, outputHex, redirected, EvaluationOptions.Default);

    public static IEnumerable<object[]> OffsetCases()
    {
        object[][] samples =
        [
            ["utc-offset-flags", 0, "printf '<%(%z|%_z|%-z|%0z|%1z|%2z|%02z|%-2z)T>' 946684800",
                Convert.ToHexString("<+0000|+   0|+0|+0000|+0000| +0000|0+0000| + 0>"u8)],
            ["positive-offset", 330, "printf '<%(%F|%H:%M|%z|%_z|%-z|%4z|%5z|%05z|%_5z|%-5z)T>' 946684800",
                Convert.ToHexString("<2000-01-01|05:30|+0530|+ 530|+530|   +0530|    +00530|0000+00530|    +  530|    +  530>"u8)],
            ["negative-offset", -210, "printf '<%(%F|%H:%M|%z|%_z|%-z|%4z|%5z|%05z|%_5z|%-5z)T>' 946684800",
                Convert.ToHexString("<1999-12-31|20:30|-0330|- 330|-330|   -0330|    -00330|0000-00330|    -  330|    -  330>"u8)],
            ["offset-modifiers", 345, "printf '<%(%Ez|%Oz|%05Ez|%-5Oz|%_Ez|%_Oz)T>' 946684800",
                Convert.ToHexString("<+0545|+0545|0000+00545|    +  545|+ 545|+ 545>"u8)],
            ["offset-flag-precedence", 0, "printf '<%(%0-5z|%-05z|%_05z|%0_5z|%_-5z|%-_5z)T>' 946684800",
                Convert.ToHexString("<    +    0|0000+00000|0000+00000|    +    0|    +    0|    +    0>"u8)],
            ["literal-offset-colons-and-name", 0, "printf '<%(%Z|%:z|%::z|%:::z|%_:z)T>' 946684800",
                Convert.ToHexString("<UTC|%:z|%::z|%:::z|%_:z>"u8)],
            ["offset-with-outer-fields", 15, "printf '<%8.4(%05z)T><%-7.4(%_z)T>' 946684800 946684800",
                Convert.ToHexString("<    0000><+  1   >"u8)],
            ["offset-count", -720, "printf -v out 'x%(%05z)T%n' 946684800 count; printf '<%s><%s><%s>' \"$?\" \"$out\" \"$count\"",
                Convert.ToHexString("<0><x0000-01200><11>"u8)],
            ["offset-limit-126", 0, "printf '<%(%63z)T>' 946684800",
                Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("<" + new string(' ', 62) + "+" + new string('0', 63) + ">"))],
            ["offset-limit-127", 0, "printf '<%(x%63z)T>' 946684800",
                Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("<x" + new string(' ', 62) + "+" + new string('0', 63) + ">"))],
            ["offset-limit-128", 0, "printf '<%(%64z)T>' 946684800", "3C3E"],
            ["offset-limit-with-literals", 0, "printf '<%(xx%63z)T>' 946684800", "3C3E"],
            ["offset-with-raw-bytes", -30, "printf $'<%(\\377%_Ez\\376%02z\\300)T>' 946684800",
                "3CFF2D20203330FE302D30303330C03E"],
        ];
        foreach (var prepared in new[] { false, true })
        foreach (var sample in samples)
            yield return [prepared, ..sample];
    }

    [Theory]
    [MemberData(nameof(OffsetCases))]
    public Task TimeOffsetsUseTheConfiguredClockAndPinnedFieldWidths(
        bool prepared, string name, int offsetMinutes, string source, string outputHex)
    {
        var options = EvaluationOptions.Default with
        {
            Timestamp = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromMinutes(offsetMinutes))
        };
        return AssertTimeFormatAsync(prepared, name, source, outputHex, redirected: false, options);
    }

    private static async Task AssertTimeFormatAsync(
        bool prepared, string name, string source, string outputHex, bool redirected, EvaluationOptions options)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source), options: options), host, owner, cancellation.Token)
            : await BashScript.RunAsync(host, source, options, cancellation.Token);
        Assert.True(result.ExitCode == 0, name);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        var files = host.CaptureEntries();
        Assert.Equal(redirected ? new[] { "out" } : [], files.Keys);
        Assert.All(files, entry =>
        {
            Assert.Equal(FilePathKind.File, entry.Value.Kind);
            Assert.Empty(entry.Value.ContentHex);
        });
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("%١Y")]
    [InlineData("%１Y")]
    [InlineData("%١١Y")]
    public void StrftimeTextGrammarPreservesNonAsciiDirectiveCharacters(string format)
    {
        Assert.True(GnuDateFormatting.TryFormat(DateTimeOffset.UnixEpoch, format,
            GnuDateFormatDialect.Strftime, 32, out var result, _ => { }));
        Assert.Equal(format, result);
    }

    [Fact]
    public void DateFlagsPreserveLeadingReplacementCaseAndFractionalWidth()
    {
        var time = DateTimeOffset.UnixEpoch.AddTicks(1_203_000);
        const string format = "%_H|%012k|%-8H|%#A|%^A|%#Z|%_12N|%-3N|%09N";

        var result = GnuDateFormatting.Format(time, format);

        Assert.Equal("  |000000000000|0|tHURSDAY|THURSDAY|utc|1203        |12|120300000", result);
    }

    [Theory]
    [InlineData(3, false, "")]
    [InlineData(9, true, "000")]
    public void DateFractionalPrefixKeepsTheGeneratedFieldAdmission(int limit, bool succeeds, string expected)
    {
        var reservations = new List<int>();

        var success = GnuDateFormatting.TryFormat(DateTimeOffset.UnixEpoch, "%3N",
            GnuDateFormatDialect.Date, limit, out var result, reservations.Add);

        Assert.Equal(succeeds, success);
        Assert.Equal(expected, result);
        Assert.Equal(succeeds ? new[] { 3, 6 } : new[] { 3 }, reservations);
    }

    [Theory]
    [InlineData(GnuDateFormatDialect.Date, "%_a", "%1a")]
    [InlineData(GnuDateFormatDialect.Date, "%#H", "%1H")]
    [InlineData(GnuDateFormatDialect.Date, "%_H", "%1H")]
    [InlineData(GnuDateFormatDialect.Date, "%#A", "%1A")]
    [InlineData(GnuDateFormatDialect.Strftime, "%#a", "%1a")]
    public void DateCaseAndPaddingReuseTheOutputBuilder(GnuDateFormatDialect dialect, string directive, string control)
    {
        var flaggedFormat = string.Concat(Enumerable.Repeat(directive, 32));
        var plainFormat = string.Concat(Enumerable.Repeat(control, 32));
        string Format(string text) => GnuDateFormatting.Format(DateTimeOffset.UnixEpoch, text, dialect);
        for (var index = 0; index < 16; index++)
        {
            _ = Format(flaggedFormat);
            _ = Format(plainFormat);
        }
        Assert.Equal(Format(plainFormat).Length, Format(flaggedFormat).Length);

        var plain = AllocationAssert.MeasureAllocatedBytes(() => Format(plainFormat), 64);
        var flagged = AllocationAssert.MeasureAllocatedBytes(() => Format(flaggedFormat), 64);

        Assert.True(flagged.Median <= plain.Median + 256,
            $"Flagged directives allocated {flagged.Median:N0} bytes and plain directives allocated {plain.Median:N0} bytes.");
    }

    [Fact]
    public void TimeFormatProjectionRequiresAdmissionBeforeItsAllocation()
    {
        var format = MakeTimeFormat(64 * 1024);
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        var result = PrintfFormatting.EvaluateCommand(format, [ShellValue.FromText("0")], owner);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output.ToArray());
        Assert.Equal("shared memory budget exhausted", Assert.Single(result.Diagnostics).ToUtf8String());
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task CancellationAfterProjectionAdmissionKeepsEarlierCountAndSkipsLaterWork()
    {
        var format = ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(0).Append("x%n"u8)
            .Append(MakeTimeFormat(32 * 1024).Bytes).Append("%n"u8).ToMemory());
        using var cancellation = new CancellationTokenSource();
        var governor = new MemoryGovernor(1024 * 1024);
        var budget = new CountingMemoryBudget(governor);
        using var owner = budget.CreateScope();
        budget.AdmissionSucceeded += () =>
        {
            if (governor.ReservedBytes >= 64 * 1024) cancellation.Cancel();
        };
        var counts = new List<PrintfVariableAssignment>();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PrintfFormatting.EvaluateAsync(format,
                [ShellValue.FromText("before"), ShellValue.FromText("0"), ShellValue.FromText("after")],
                DateTimeOffset.UnixEpoch, assignOutput: false, owner,
                count => { counts.Add(count); return ValueTask.FromResult(true); },
                _ => throw new InvalidOperationException("Unreached diagnostic."), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(new[] { new PrintfVariableAssignment("before", 1) }, counts);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void TimeResultRemainsAdmittedAfterTemporaryRenderingEnds()
    {
        const int Bytes = 64;
        var format = MakeTimeFormat(Bytes);
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        var result = PrintfFormatting.EvaluateCommand(format, [ShellValue.FromText("0")], owner);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Enumerable.Repeat((byte)0xff, Bytes).ToArray(), result.Output.ToArray());
        Assert.True(governor.ReservedBytes >= Bytes);
        Assert.True(governor.PeakBytes > governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void ReusedTimeConversionsReuseTheirTemporaryWorkspace()
    {
        const int Bytes = 64;
        const int Count = 256;
        var format = MakeTimeFormat(Bytes);
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        var result = PrintfFormatting.EvaluateCommand(format,
            Enumerable.Repeat(ShellValue.FromText("0"), Count).ToArray(), owner);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Enumerable.Repeat((byte)0xff, Bytes * Count).ToArray(), result.Output.ToArray());
        Assert.Empty(result.Diagnostics);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private static ShellValue MakeTimeFormat(int bytes)
    {
        var format = new byte[bytes + 4];
        format[0] = (byte)'%';
        format[1] = (byte)'(';
        format.AsSpan(2, bytes).Fill(0xff);
        format[^2] = (byte)')';
        format[^1] = (byte)'T';
        return ShellValue.FromBytes(format);
    }
}
