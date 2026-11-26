using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class PrintfNumericTests
{
    // Pinned Linux Bash 5.2.21. MSYS 5.2.37 agrees except the documented
    // extended-float spelling, NaN payload, underflow and signed-zero profiles.
    // Character constants use the pinned Linux C.utf8 profile.
    public static IEnumerable<object[]> NumericCases()
    {
        var cases = new object[][]
        {
            ["numeric-byte-signed", "printf 'before:%d:after' $'\\377'; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A303A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A20FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-v-signed", "v=old; printf -v v 'before:%d:after' $'\\377'; printf '|status:%s|v:<%s>|tail' \"$?\" \"$v\"", 0, "7C7374617475733A317C763A3C6265666F72653A303A61667465723E7C7461696C", "6C696E6520313A207072696E74663A20FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-unsigned", "printf 'before:%u:after' $'12\\377'; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A31323A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-v-unsigned", "v=old; printf -v v 'before:%u:after' $'12\\377'; printf '|status:%s|v:<%s>|tail' \"$?\" \"$v\"", 0, "7C7374617475733A317C763A3C6265666F72653A31323A61667465723E7C7461696C", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-float", "printf 'before:%f:after' $'1.5\\377'; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A312E3530303030303A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A20312E35FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-v-float", "v=old; printf -v v 'before:%f:after' $'1.5\\377'; printf '|status:%s|v:<%s>|tail' \"$?\" \"$v\"", 0, "7C7374617475733A317C763A3C6265666F72653A312E3530303030303A61667465723E7C7461696C", "6C696E6520313A207072696E74663A20312E35FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-width", "printf 'before:%*s:after' $'2\\377' x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A20783A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A2032FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-v-width", "v=old; printf -v v 'before:%*s:after' $'2\\377' x; printf '|status:%s|v:<%s>|tail' \"$?\" \"$v\"", 0, "7C7374617475733A317C763A3C6265666F72653A20783A61667465723E7C7461696C", "6C696E6520313A207072696E74663A2032FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-precision", "printf 'before:%.*s:after' $'2\\377' xyz; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A78793A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A2032FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-v-precision", "v=old; printf -v v 'before:%.*s:after' $'2\\377' xyz; printf '|status:%s|v:<%s>|tail' \"$?\" \"$v\"", 0, "7C7374617475733A317C763A3C6265666F72653A78793A61667465723E7C7461696C", "6C696E6520313A207072696E74663A2032FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-time", "printf 'before:%(%s)T:after' $'12\\377'; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A31323A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-v-time", "v=old; printf -v v 'before:%(%s)T:after' $'12\\377'; printf '|status:%s|v:<%s>|tail' \"$?\" \"$v\"", 0, "7C7374617475733A317C763A3C6265666F72653A31323A61667465723E7C7461696C", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-character-d", "printf '%d' $'\\\"\\377'; printf '|status:%s|tail' \"$?\"", 0, "3235357C7374617475733A307C7461696C", "", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-character-u", "printf '%u' $'\\\"\\377'; printf '|status:%s|tail' \"$?\"", 0, "3235357C7374617475733A307C7461696C", "", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-byte-character-f", "printf '%f' $'\\\"\\377'; printf '|status:%s|tail' \"$?\"", 0, "3235352E3030303030307C7374617475733A307C7461696C", "", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-closed-error", "printf '<%d>' $'\\377' 2>&-; printf '|status:%s|tail' \"$?\"", 0, "3C303E7C7374617475733A317C7461696C", "", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\"}", "a|b|c"],
            ["numeric-redirect-error", "printf '<%d>' $'\\377' 2>err; printf '|status:%s|tail' \"$?\"", 0, "3C303E7C7374617475733A317C7461696C", "", "{\"a\":\"Directory:\",\"b\":\"Directory:\",\"c\":\"Directory:\",\"err\":\"File:6C696E6520313A207072696E74663A20FF3A20696E76616C6964206E756D6265720A\"}", "a|b|c"],
            ["parse-17", "printf 'before:<%d>:after' 0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-18", "printf 'before:<%d>:after' -0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-19", "printf 'before:<%d>:after' +0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-20", "printf 'before:<%d>:after' 0x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A2030783A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["parse-21", "printf 'before:<%d>:after' -0x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A202D30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-22", "printf 'before:<%d>:after' 0x1p+; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C313E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A20307831702B3A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["parse-23", "printf 'before:<%d>:after' 09; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A2030393A20696E76616C6964206F6374616C206E756D6265720A", "{}", ""],
            ["parse-24", "printf 'before:<%d>:after' 0x1.8p1; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C313E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203078312E3870313A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["parse-25", "printf 'before:<%d>:after' 12x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C31323E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203132783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-26", "printf 'before:<%d>:after' $'12\\377'; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C31323E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-27", "printf 'before:<%d>:after' 9223372036854775807; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C393232333337323033363835343737353830373E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-28", "printf 'before:<%d>:after' 9223372036854775808; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C393232333337323033363835343737353830373E3A61667465727C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A20393232333337323033363835343737353830383A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["parse-29", "printf 'before:<%d>:after' -9223372036854775809; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C2D393232333337323033363835343737353830383E3A61667465727C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A202D393232333337323033363835343737353830393A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["parse-30", "printf 'before:<%d>:after' 18446744073709551615; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C393232333337323033363835343737353830373E3A61667465727C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A2031383434363734343037333730393535313631353A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["parse-31", "printf 'before:<%d>:after' 18446744073709551616; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C393232333337323033363835343737353830373E3A61667465727C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A2031383434363734343037333730393535313631363A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["parse-32", "printf 'before:<%d>:after' -18446744073709551616; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C2D393232333337323033363835343737353830383E3A61667465727C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A202D31383434363734343037333730393535313631363A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["parse-33", "printf 'before:<%u>:after' 0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-34", "printf 'before:<%u>:after' -0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-35", "printf 'before:<%u>:after' +0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-36", "printf 'before:<%u>:after' 0x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A2030783A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["parse-37", "printf 'before:<%u>:after' -0x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A202D30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-38", "printf 'before:<%u>:after' 0x1p+; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C313E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A20307831702B3A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["parse-39", "printf 'before:<%u>:after' 09; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A2030393A20696E76616C6964206F6374616C206E756D6265720A", "{}", ""],
            ["parse-40", "printf 'before:<%u>:after' 0x1.8p1; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C313E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203078312E3870313A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["parse-41", "printf 'before:<%u>:after' 12x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C31323E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203132783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-42", "printf 'before:<%u>:after' $'12\\377'; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C31323E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-43", "printf 'before:<%u>:after' 9223372036854775807; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C393232333337323033363835343737353830373E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-44", "printf 'before:<%u>:after' 9223372036854775808; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C393232333337323033363835343737353830383E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-45", "printf 'before:<%u>:after' -9223372036854775809; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C393232333337323033363835343737353830373E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-46", "printf 'before:<%u>:after' 18446744073709551615; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C31383434363734343037333730393535313631353E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-47", "printf 'before:<%u>:after' 18446744073709551616; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C31383434363734343037333730393535313631353E3A61667465727C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A2031383434363734343037333730393535313631363A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["parse-48", "printf 'before:<%u>:after' -18446744073709551616; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C31383434363734343037333730393535313631353E3A61667465727C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A202D31383434363734343037333730393535313631363A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["parse-49", "printf 'before:<%a>:after' 0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307830702B303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-50", "printf 'before:<%a>:after' -0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C2D307830702B303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-51", "printf 'before:<%a>:after' +0; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307830702B303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-52", "printf 'before:<%a>:after' 0x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307830702B303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A2030783A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["parse-53", "printf 'before:<%a>:after' -0x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C2D307830702B303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A202D30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-54", "printf 'before:<%a>:after' 0x1p+; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307838702D333E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A20307831702B3A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["parse-55", "printf 'before:<%a>:after' 09; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307839702B303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-56", "printf 'before:<%a>:after' 0x1.8p1; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307863702D323E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-57", "printf 'before:<%a>:after' 12x; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307863702B303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203132783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-58", "printf 'before:<%a>:after' $'12\\377'; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307863702B303E3A61667465727C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["parse-59", "printf 'before:<%a>:after' 9223372036854775807; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C3078662E666666666666666666666666666665702B35393E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-60", "printf 'before:<%a>:after' 9223372036854775808; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307838702B36303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-61", "printf 'before:<%a>:after' -9223372036854775809; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C2D3078382E303030303030303030303030303031702B36303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-62", "printf 'before:<%a>:after' 18446744073709551615; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C3078662E666666666666666666666666666666702B36303E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-63", "printf 'before:<%a>:after' 18446744073709551616; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C307838702B36313E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["parse-64", "printf 'before:<%a>:after' -18446744073709551616; printf '|status:%s|tail' \"$?\"", 0, "6265666F72653A3C2D307838702B36313E3A61667465727C7374617475733A307C7461696C", "", "{}", ""],
            ["float-65", "printf '<%a>' inf; printf '|status:%s|tail' \"$?\"", 0, "3C696E663E7C7374617475733A307C7461696C", "", "{}", ""],
            ["float-66", "printf '<%a>' infinity; printf '|status:%s|tail' \"$?\"", 0, "3C696E663E7C7374617475733A307C7461696C", "", "{}", ""],
            ["float-67", "printf '<%a>' INFx; printf '|status:%s|tail' \"$?\"", 0, "3C696E663E7C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A20494E46783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["float-68", "printf '<%a>' nan; printf '|status:%s|tail' \"$?\"", 0, "3C6E616E3E7C7374617475733A307C7461696C", "", "{}", ""],
            ["float-69", "printf '<%a>' 'nan()'; printf '|status:%s|tail' \"$?\"", 0, "3C6E616E3E7C7374617475733A307C7461696C", "", "{}", ""],
            ["float-70", "printf '<%a>' 'nan(foo_bar9)'; printf '|status:%s|tail' \"$?\"", 0, "3C6E616E3E7C7374617475733A307C7461696C", "", "{}", ""],
            ["float-71", "printf '<%a>' 'nan(bad-payload)'; printf '|status:%s|tail' \"$?\"", 0, "3C6E616E3E7C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A206E616E286261642D7061796C6F6164293A20696E76616C6964206E756D6265720A", "{}", ""],
            ["float-72", "printf '<%a>' 1e9999; printf '|status:%s|tail' \"$?\"", 0, "3C696E663E7C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A203165393939393A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["float-73", "printf '<%a>' 1e-9999; printf '|status:%s|tail' \"$?\"", 0, "3C307830702B303E7C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A2031652D393939393A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["float-74", "printf '<%a>' 0x1p-16445; printf '|status:%s|tail' \"$?\"", 0, "3C3078302E303030303030303030303030303031702D31363338353E7C7374617475733A307C7461696C", "", "{}", ""],
            ["float-75", "printf '<%a>' 0x1p-16446; printf '|status:%s|tail' \"$?\"", 0, "3C307830702B303E7C7374617475733A307C7461696C", "6C696E6520313A207072696E74663A207761726E696E673A20307831702D31363434363A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["float-76", "printf '<%a>' -x; printf '|status:%s|tail' \"$?\"", 0, "3C307830702B303E7C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A202D783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["float-77", "printf '<%a>' $'nan(foo)\\377'; printf '|status:%s|tail' \"$?\"", 0, "3C6E616E3E7C7374617475733A317C7461696C", "6C696E6520313A207072696E74663A206E616E28666F6F29FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["float-78", "printf '<%a>' \"'A\"; printf '|status:%s|tail' \"$?\"", 0, "3C3078382E32702B333E7C7374617475733A307C7461696C", "", "{}", ""],
            ["float-79", "printf '<%a>' \"'\"; printf '|status:%s|tail' \"$?\"", 0, "3C307830702B303E7C7374617475733A307C7461696C", "", "{}", ""],
            ["ordered-80", "n=old; printf 'a%n%d%n' before $'12\\377' after; printf '|%s:%s:%s' \"$?\" \"$before\" \"$after\"", 0, "6131327C313A313A33", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["ordered-81", "n=old; printf 'a%n%u%n' before $'12\\377' after; printf '|%s:%s:%s' \"$?\" \"$before\" \"$after\"", 0, "6131327C313A313A33", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["ordered-82", "n=old; printf 'a%n%f%n' before $'12\\377' after; printf '|%s:%s:%s' \"$?\" \"$before\" \"$after\"", 0, "6131322E3030303030307C313A313A3130", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["ordered-83", "n=old; printf 'a%n%*s%n' before $'12\\377' x after; printf '|%s:%s:%s' \"$?\" \"$before\" \"$after\"", 0, "612020202020202020202020787C313A313A3133", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["ordered-84", "n=old; printf 'a%n%.*s%n' before $'12\\377' x after; printf '|%s:%s:%s' \"$?\" \"$before\" \"$after\"", 0, "61787C313A313A32", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["ordered-85", "n=old; printf 'a%n%(%s)T%n' before $'12\\377' after; printf '|%s:%s:%s' \"$?\" \"$before\" \"$after\"", 0, "6131327C313A313A33", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["char-86", "printf '<%d>' $'\\377'; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A20FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["char-87", "printf '<%d>' $'12\\377'; printf '|%s' \"$?\"", 0, "3C31323E7C31", "6C696E6520313A207072696E74663A203132FF3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["char-88", "printf '<%d>' $'\\\"\\377'; printf '|%s' \"$?\"", 0, "3C3235353E7C30", "", "{}", ""],
            ["char-89", "printf '<%d>' $'\\\"a\\377'; printf '|%s' \"$?\"", 0, "3C39373E7C30", "", "{}", ""],
            ["invalid-format-byte", "printf $'before%12\\377after'; printf '|%s' \"$?\"", 0, "6265666F72657C31", "6C696E6520313A207072696E74663A2060FF273A20696E76616C696420666F726D6174206368617261637465720A", "{}", ""],
            ["long-integer", "printf -v n '%4096s' ''; n=${n// /9}; printf '<%u>' \"$n\" 2>/dev/null; printf '|%s' \"$?\"", 0, "3C31383434363734343037333730393535313631353E7C30", "", "{}", ""],
            ["long-hexfloat", "printf -v n '%8192s' ''; n=${n// /f}; printf '<%a>' \"0x${n}p0\" 2>/dev/null; printf '|%s' \"$?\"", 0, "3C696E663E7C30", "", "{}", ""],
            ["diagnostic-0", "printf '<%d>' 0x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A2030783A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["diagnostic-1", "printf '<%d>' -0x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A202D30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-2", "printf '<%d>' +0x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A202B30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-3", "printf '<%d>' ' 0x'; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A202030783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-4", "printf '<%d>' 0x1p+; printf '|%s' \"$?\"", 0, "3C313E7C31", "6C696E6520313A207072696E74663A20307831702B3A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["diagnostic-5", "printf '<%d>' -0x1p+; printf '|%s' \"$?\"", 0, "3C2D313E7C31", "6C696E6520313A207072696E74663A202D307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-6", "printf '<%d>' +0x1p+; printf '|%s' \"$?\"", 0, "3C313E7C31", "6C696E6520313A207072696E74663A202B307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-7", "printf '<%d>' ' 0x1p+'; printf '|%s' \"$?\"", 0, "3C313E7C31", "6C696E6520313A207072696E74663A2020307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-8", "printf '<%d>' 09x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A203039783A20696E76616C6964206F6374616C206E756D6265720A", "{}", ""],
            ["diagnostic-9", "printf '<%d>' -09x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A202D3039783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-10", "printf '<%d>' 00z; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A2030307A3A20696E76616C6964206F6374616C206E756D6265720A", "{}", ""],
            ["diagnostic-11", "printf '<%d>' 0.2x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A20302E32783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-12", "printf '<%u>' 0x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A2030783A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["diagnostic-13", "printf '<%u>' -0x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A202D30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-14", "printf '<%u>' +0x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A202B30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-15", "printf '<%u>' ' 0x'; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A202030783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-16", "printf '<%u>' 0x1p+; printf '|%s' \"$?\"", 0, "3C313E7C31", "6C696E6520313A207072696E74663A20307831702B3A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["diagnostic-17", "printf '<%u>' -0x1p+; printf '|%s' \"$?\"", 0, "3C31383434363734343037333730393535313631353E7C31", "6C696E6520313A207072696E74663A202D307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-18", "printf '<%u>' +0x1p+; printf '|%s' \"$?\"", 0, "3C313E7C31", "6C696E6520313A207072696E74663A202B307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-19", "printf '<%u>' ' 0x1p+'; printf '|%s' \"$?\"", 0, "3C313E7C31", "6C696E6520313A207072696E74663A2020307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-20", "printf '<%u>' 09x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A203039783A20696E76616C6964206F6374616C206E756D6265720A", "{}", ""],
            ["diagnostic-21", "printf '<%u>' -09x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A202D3039783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-22", "printf '<%u>' 00z; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A2030307A3A20696E76616C6964206F6374616C206E756D6265720A", "{}", ""],
            ["diagnostic-23", "printf '<%u>' 0.2x; printf '|%s' \"$?\"", 0, "3C303E7C31", "6C696E6520313A207072696E74663A20302E32783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-24", "printf '<%f>' 0x; printf '|%s' \"$?\"", 0, "3C302E3030303030303E7C31", "6C696E6520313A207072696E74663A2030783A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["diagnostic-25", "printf '<%f>' -0x; printf '|%s' \"$?\"", 0, "3C2D302E3030303030303E7C31", "6C696E6520313A207072696E74663A202D30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-26", "printf '<%f>' +0x; printf '|%s' \"$?\"", 0, "3C302E3030303030303E7C31", "6C696E6520313A207072696E74663A202B30783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-27", "printf '<%f>' ' 0x'; printf '|%s' \"$?\"", 0, "3C302E3030303030303E7C31", "6C696E6520313A207072696E74663A202030783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-28", "printf '<%f>' 0x1p+; printf '|%s' \"$?\"", 0, "3C312E3030303030303E7C31", "6C696E6520313A207072696E74663A20307831702B3A20696E76616C696420686578206E756D6265720A", "{}", ""],
            ["diagnostic-29", "printf '<%f>' -0x1p+; printf '|%s' \"$?\"", 0, "3C2D312E3030303030303E7C31", "6C696E6520313A207072696E74663A202D307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-30", "printf '<%f>' +0x1p+; printf '|%s' \"$?\"", 0, "3C312E3030303030303E7C31", "6C696E6520313A207072696E74663A202B307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-31", "printf '<%f>' ' 0x1p+'; printf '|%s' \"$?\"", 0, "3C312E3030303030303E7C31", "6C696E6520313A207072696E74663A2020307831702B3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-32", "printf '<%f>' 09x; printf '|%s' \"$?\"", 0, "3C392E3030303030303E7C31", "6C696E6520313A207072696E74663A203039783A20696E76616C6964206F6374616C206E756D6265720A", "{}", ""],
            ["diagnostic-33", "printf '<%f>' -09x; printf '|%s' \"$?\"", 0, "3C2D392E3030303030303E7C31", "6C696E6520313A207072696E74663A202D3039783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["diagnostic-34", "printf '<%f>' 00z; printf '|%s' \"$?\"", 0, "3C302E3030303030303E7C31", "6C696E6520313A207072696E74663A2030307A3A20696E76616C6964206F6374616C206E756D6265720A", "{}", ""],
            ["diagnostic-35", "printf '<%f>' 0.2x; printf '|%s' \"$?\"", 0, "3C302E3230303030303E7C31", "6C696E6520313A207072696E74663A20302E32783A20696E76616C6964206E756D6265720A", "{}", ""],
            ["character-0", "value=$'\\\"\\u03bb'; printf '%d:%f:%u' \"$value\" \"$value\" \"$value\"; printf '|%s' \"$?\"", 0, "3935353A3935352E3030303030303A3935357C30", "", "{}", ""],
            ["character-1", "value=$'\\\"\\U0001f642'; printf '%d:%f:%u' \"$value\" \"$value\" \"$value\"; printf '|%s' \"$?\"", 0, "3132383537383A3132383537382E3030303030303A3132383537387C30", "", "{}", ""],
            ["character-2", "value=$'\\\"\\377'; printf '%d:%f:%u' \"$value\" \"$value\" \"$value\"; printf '|%s' \"$?\"", 0, "3235353A3235352E3030303030303A3235357C30", "", "{}", ""],
            ["character-3", "value=$'\\\"\\uD800'; printf '%d:%f:%u' \"$value\" \"$value\" \"$value\"; printf '|%s' \"$?\"", 0, "3233373A3233372E3030303030303A3233377C30", "", "{}", ""],
            ["character-4", "value=$'\\\"\\u00e9\\377'; printf '%d:%f:%u' \"$value\" \"$value\" \"$value\"; printf '|%s' \"$?\"", 0, "3233333A3233332E3030303030303A3233337C30", "", "{}", ""],
            ["character-5", "value=$'\\\"a\\377'; printf '%d:%f:%u' \"$value\" \"$value\" \"$value\"; printf '|%s' \"$?\"", 0, "39373A39372E3030303030303A39377C30", "", "{}", ""],
            ["floating-range-decimal-over", "printf '<%a>' 'nan(18446744073709551616)'; printf '|%s\\n' \"$?\"\n", 0, "3C6E616E3E7C300A", "6C696E6520313A207072696E74663A207761726E696E673A206E616E283138343436373434303733373039353531363136293A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["floating-range-hex-limit", "printf '<%a>' 'nan(0xffffffffffffffff)'; printf '|%s\\n' \"$?\"\n", 0, "3C6E616E3E7C300A", "", "{}", ""],
            ["floating-range-text-inside", "printf '<%a>' 'nan(18446744073709551616extra)'; printf '|%s\\n' \"$?\"\n", 0, "3C6E616E3E7C300A", "6C696E6520313A207072696E74663A207761726E696E673A206E616E2831383434363734343037333730393535313631366578747261293A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["floating-range-text-outside", "printf '<%a>' 'nan(18446744073709551616)extra'; printf '|%s\\n' \"$?\"\n", 0, "3C6E616E3E7C310A", "6C696E6520313A207072696E74663A206E616E2831383434363734343037333730393535313631362965787472613A20696E76616C6964206E756D6265720A", "{}", ""],
            ["floating-range-assigned", "printf -v v '<%F>' '-nan(18446744073709551616)'; s=$?; printf '%s|%s\\n' \"$v\" \"$s\"\n", 0, "3C2D4E414E3E7C300A", "6C696E6520313A207072696E74663A207761726E696E673A202D6E616E283138343436373434303733373039353531363136293A204E756D65726963616C20726573756C74206F7574206F662072616E67650A", "{}", ""],
            ["floating-range-closed-stderr", "printf '<%a>' 'nan(18446744073709551616)' 2>&-; printf '|%s\\n' \"$?\"\n", 0, "3C6E616E3E7C300A", "", "{}", ""],
            ["floating-range-finite-over-tail", "printf '<%a>' '1e9999tail'; printf '|%s\\n' \"$?\"\n", 0, "3C696E663E7C310A", "6C696E6520313A207072696E74663A203165393939397461696C3A20696E76616C6964206E756D6265720A", "{}", ""],
            ["floating-range-hex-float-over-tail", "printf '<%a>' '0x1p99999tail'; printf '|%s\\n' \"$?\"\n", 0, "3C696E663E7C310A", "6C696E6520313A207072696E74663A203078317039393939397461696C3A20696E76616C696420686578206E756D6265720A", "{}", ""],
        };
        foreach (var prepared in new[] { false, true })
        foreach (var sample in cases)
            yield return [prepared, ..sample];
    }

    [Theory]
    [MemberData(nameof(NumericCases))]
    public async Task NumericConversionsPreserveBytesStatusEffectsAndDiagnostics(
        bool prepared, string name, string script, int status, string outputHex,
        string diagnosticHex, string filesJson, string directories)
    {
        var memory = new MemoryGovernor(1024 * 1024);
        using var owner = memory.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        foreach (var path in directories.Split('|', StringSplitOptions.RemoveEmptyEntries))
            host.AddDirectory("/" + path);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancel.Token)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, cancel.Token);
        Assert.True(result.ExitCode == status, name);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(diagnosticHex), host.StandardError.Content.ToArray());
        var files = JsonSerializer.Deserialize<SortedDictionary<string, string>>(filesJson)
            ?? throw new InvalidDataException();
        Assert.Equal(files.ToArray(), host.CaptureEntries().Select(p =>
            new KeyValuePair<string, string>(p.Key, p.Value.Kind + ":" + p.Value.ContentHex)).ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }
}
