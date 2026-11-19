using Lokad.Bash.Evaluation;
using Lokad.Bash.Formatting;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class PrintfFieldSizeTests
{
    // File-backed pinned Linux Bash 5.2.21; MSYS 5.2.37 differs only on hex-float spelling.
    private static readonly object[][] Samples =
    [
        ["string-1048576", "printf -v v '%.1048576s' $'x\\377'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C78FF3E", ""],
        ["string-1048576-dynamic", "printf -v v '%.*s' 1048576 $'x\\377'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C78FF3E", ""],
        ["escape-1048576", "printf -v v '%.1048576b' 'x\\n'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C780A3E", ""],
        ["escape-1048576-dynamic", "printf -v v '%.*b' 1048576 'x\\n'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C780A3E", ""],
        ["quote-1048576", "printf -v v '%.1048576q' ''; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C27273E", ""],
        ["quote-1048576-dynamic", "printf -v v '%.*q' 1048576 ''; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C27273E", ""],
        ["quoted-input-1048576", "printf -v v '%.1048576Q' 'a b'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C615C20623E", ""],
        ["quoted-input-1048576-dynamic", "printf -v v '%.*Q' 1048576 'a b'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C615C20623E", ""],
        ["character-1048576", "printf -v v '%.1048576c' 'x'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C783E", ""],
        ["character-1048576-dynamic", "printf -v v '%.*c' 1048576 'x'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C783E", ""],
        ["time-1048576", "printf -v v '%.1048576(%s)T' 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["time-1048576-dynamic", "printf -v v '%.*(%s)T' 1048576 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["general-1048576", "printf -v v '%.1048576g' 1.25; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C312E32353E", ""],
        ["general-1048576-dynamic", "printf -v v '%.*g' 1048576 1.25; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C312E32353E", ""],
        ["general-zero-1048576", "printf -v v '%.1048576G' 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["general-zero-1048576-dynamic", "printf -v v '%.*G' 1048576 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["infinity-1048576", "printf -v v '%.1048576f' inf; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A332C76616C75653A3C696E663E", ""],
        ["infinity-1048576-dynamic", "printf -v v '%.*f' 1048576 inf; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A332C76616C75653A3C696E663E", ""],
        ["count-1048576-precision", "n=old; printf 'a%.1048576nZ' n; printf '|%s:%s' \"$?\" \"$n\"", 0, "615A7C303A31", ""],
        ["count-1048576-width", "n=old; printf 'a%1048576nZ' n; printf '|%s:%s' \"$?\" \"$n\"", 0, "615A7C303A31", ""],
        ["string-1048577", "printf -v v '%.1048577s' $'x\\377'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C78FF3E", ""],
        ["string-1048577-dynamic", "printf -v v '%.*s' 1048577 $'x\\377'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C78FF3E", ""],
        ["escape-1048577", "printf -v v '%.1048577b' 'x\\n'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C780A3E", ""],
        ["escape-1048577-dynamic", "printf -v v '%.*b' 1048577 'x\\n'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C780A3E", ""],
        ["quote-1048577", "printf -v v '%.1048577q' ''; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C27273E", ""],
        ["quote-1048577-dynamic", "printf -v v '%.*q' 1048577 ''; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C27273E", ""],
        ["quoted-input-1048577", "printf -v v '%.1048577Q' 'a b'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C615C20623E", ""],
        ["quoted-input-1048577-dynamic", "printf -v v '%.*Q' 1048577 'a b'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C615C20623E", ""],
        ["character-1048577", "printf -v v '%.1048577c' 'x'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C783E", ""],
        ["character-1048577-dynamic", "printf -v v '%.*c' 1048577 'x'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C783E", ""],
        ["time-1048577", "printf -v v '%.1048577(%s)T' 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["time-1048577-dynamic", "printf -v v '%.*(%s)T' 1048577 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["general-1048577", "printf -v v '%.1048577g' 1.25; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C312E32353E", ""],
        ["general-1048577-dynamic", "printf -v v '%.*g' 1048577 1.25; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C312E32353E", ""],
        ["general-zero-1048577", "printf -v v '%.1048577G' 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["general-zero-1048577-dynamic", "printf -v v '%.*G' 1048577 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["infinity-1048577", "printf -v v '%.1048577f' inf; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A332C76616C75653A3C696E663E", ""],
        ["infinity-1048577-dynamic", "printf -v v '%.*f' 1048577 inf; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A332C76616C75653A3C696E663E", ""],
        ["count-1048577-precision", "n=old; printf 'a%.1048577nZ' n; printf '|%s:%s' \"$?\" \"$n\"", 0, "615A7C303A31", ""],
        ["count-1048577-width", "n=old; printf 'a%1048577nZ' n; printf '|%s:%s' \"$?\" \"$n\"", 0, "615A7C303A31", ""],
        ["string-2147483647", "printf -v v '%.2147483647s' $'x\\377'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C78FF3E", ""],
        ["string-2147483647-dynamic", "printf -v v '%.*s' 2147483647 $'x\\377'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C78FF3E", ""],
        ["escape-2147483647", "printf -v v '%.2147483647b' 'x\\n'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C780A3E", ""],
        ["escape-2147483647-dynamic", "printf -v v '%.*b' 2147483647 'x\\n'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C780A3E", ""],
        ["quote-2147483647", "printf -v v '%.2147483647q' ''; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C27273E", ""],
        ["quote-2147483647-dynamic", "printf -v v '%.*q' 2147483647 ''; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A322C76616C75653A3C27273E", ""],
        ["quoted-input-2147483647", "printf -v v '%.2147483647Q' 'a b'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C615C20623E", ""],
        ["quoted-input-2147483647-dynamic", "printf -v v '%.*Q' 2147483647 'a b'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C615C20623E", ""],
        ["character-2147483647", "printf -v v '%.2147483647c' 'x'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C783E", ""],
        ["character-2147483647-dynamic", "printf -v v '%.*c' 2147483647 'x'; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C783E", ""],
        ["time-2147483647", "printf -v v '%.2147483647(%s)T' 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["time-2147483647-dynamic", "printf -v v '%.*(%s)T' 2147483647 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["general-2147483647", "printf -v v '%.2147483647g' 1.25; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C312E32353E", ""],
        ["general-2147483647-dynamic", "printf -v v '%.*g' 2147483647 1.25; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A342C76616C75653A3C312E32353E", ""],
        ["general-zero-2147483647", "printf -v v '%.2147483647G' 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["general-zero-2147483647-dynamic", "printf -v v '%.*G' 2147483647 0; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A312C76616C75653A3C303E", ""],
        ["infinity-2147483647", "printf -v v '%.2147483647f' inf; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A332C76616C75653A3C696E663E", ""],
        ["infinity-2147483647-dynamic", "printf -v v '%.*f' 2147483647 inf; printf 'status:%s,length:%s,value:<%s>' \"$?\" \"${#v}\" \"$v\"", 0, "7374617475733A302C6C656E6774683A332C76616C75653A3C696E663E", ""],
        ["count-2147483647-precision", "n=old; printf 'a%.2147483647nZ' n; printf '|%s:%s' \"$?\" \"$n\"", 0, "615A7C303A31", ""],
        ["count-2147483647-width", "n=old; printf 'a%2147483647nZ' n; printf '|%s:%s' \"$?\" \"$n\"", 0, "615A7C303A31", ""],
        ["large-width-s", "printf -v v '%1048577s%n' x n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:4}\" \"${v: -4}\"", 0, "303A313034383537373A313034383537373A3C202020203E3A3C202020783E", ""],
        ["large-width-d", "printf -v v '%1048577d%n' 7 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:4}\" \"${v: -4}\"", 0, "303A313034383537373A313034383537373A3C202020203E3A3C202020373E", ""],
        ["large-width-x", "printf -v v '%1048577x%n' 31 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:4}\" \"${v: -4}\"", 0, "303A313034383537373A313034383537373A3C202020203E3A3C202031663E", ""],
        ["large-precision-d", "printf -v v '%.1048577d%n' 7 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:8}\" \"${v: -8}\"", 0, "303A313034383537373A313034383537373A3C30303030303030303E3A3C30303030303030373E", ""],
        ["large-precision-u", "printf -v v '%.1048577u%n' 7 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:8}\" \"${v: -8}\"", 0, "303A313034383537373A313034383537373A3C30303030303030303E3A3C30303030303030373E", ""],
        ["large-precision-o", "printf -v v '%.1048577o%n' 7 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:8}\" \"${v: -8}\"", 0, "303A313034383537373A313034383537373A3C30303030303030303E3A3C30303030303030373E", ""],
        ["large-precision-x", "printf -v v '%.1048577x%n' 31 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:8}\" \"${v: -8}\"", 0, "303A313034383537373A313034383537373A3C30303030303030303E3A3C30303030303031663E", ""],
        ["large-precision-f", "printf -v v '%.1048577f%n' 1.25 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:8}\" \"${v: -8}\"", 0, "303A313034383537393A313034383537393A3C312E3235303030303E3A3C30303030303030303E", ""],
        ["large-precision-e", "printf -v v '%.1048577e%n' 1.25 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:8}\" \"${v: -8}\"", 0, "303A313034383538333A313034383538333A3C312E3235303030303E3A3C30303030652B30303E", ""],
        ["large-precision-a", "printf -v v '%.1048577a%n' 1.25 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:8}\" \"${v: -8}\"", 0, "303A313034383538343A313034383538343A3C3078612E303030303E3A3C3030303030702D333E", ""],
        ["large-precision-g", "printf -v v '%#.1048577g%n' 1.25 n; printf '%s:%s:%s:<%s>:<%s>' \"$?\" \"${#v}\" \"$n\" \"${v:0:8}\" \"${v: -8}\"", 0, "303A313034383537383A313034383537383A3C312E3235303030303E3A3C30303030303030303E", ""],
        ["finite-71", "printf '<%#.20f>' 0; printf '|%s' \"$?\"", 0, "3C302E30303030303030303030303030303030303030303E7C30", ""],
        ["finite-72", "printf '<%#.20e>' 0; printf '|%s' \"$?\"", 0, "3C302E3030303030303030303030303030303030303030652B30303E7C30", ""],
        ["finite-73", "printf '<%#.20g>' 0; printf '|%s' \"$?\"", 0, "3C302E303030303030303030303030303030303030303E7C30", ""],
        ["finite-74", "printf '<%#.20a>' 0; printf '|%s' \"$?\"", 0, "3C3078302E3030303030303030303030303030303030303030702B303E7C30", ""],
        ["finite-75", "printf '<%#.20f>' 1.25; printf '|%s' \"$?\"", 0, "3C312E32353030303030303030303030303030303030303E7C30", ""],
        ["finite-76", "printf '<%#.20e>' 1.25; printf '|%s' \"$?\"", 0, "3C312E3235303030303030303030303030303030303030652B30303E7C30", ""],
        ["finite-77", "printf '<%#.20g>' 1.25; printf '|%s' \"$?\"", 0, "3C312E323530303030303030303030303030303030303E7C30", ""],
        ["finite-78", "printf '<%#.20a>' 1.25; printf '|%s' \"$?\"", 0, "3C3078612E3030303030303030303030303030303030303030702D333E7C30", ""],
        ["finite-79", "printf '<%#.20f>' 9.9999; printf '|%s' \"$?\"", 0, "3C392E39393938393939393939393939393939393937343E7C30", ""],
        ["finite-80", "printf '<%#.20e>' 9.9999; printf '|%s' \"$?\"", 0, "3C392E3939393839393939393939393939393939393734652B30303E7C30", ""],
        ["finite-81", "printf '<%#.20g>' 9.9999; printf '|%s' \"$?\"", 0, "3C392E393939383939393939393939393939393939373E7C30", ""],
        ["finite-82", "printf '<%#.20a>' 9.9999; printf '|%s' \"$?\"", 0, "3C3078392E6666663937323437343533386566333030303030702B303E7C30", ""],
        ["finite-83", "printf '<%#.20f>' 1000; printf '|%s' \"$?\"", 0, "3C313030302E30303030303030303030303030303030303030303E7C30", ""],
        ["finite-84", "printf '<%#.20e>' 1000; printf '|%s' \"$?\"", 0, "3C312E3030303030303030303030303030303030303030652B30333E7C30", ""],
        ["finite-85", "printf '<%#.20g>' 1000; printf '|%s' \"$?\"", 0, "3C313030302E303030303030303030303030303030303E7C30", ""],
        ["finite-86", "printf '<%#.20a>' 1000; printf '|%s' \"$?\"", 0, "3C3078662E6130303030303030303030303030303030303030702B363E7C30", ""],
        ["finite-87", "printf '<%#.20f>' 0.00001; printf '|%s' \"$?\"", 0, "3C302E30303030313030303030303030303030303030303E7C30", ""],
        ["finite-88", "printf '<%#.20e>' 0.00001; printf '|%s' \"$?\"", 0, "3C392E3939393939393939393939393939393939393935652D30363E7C30", ""],
        ["finite-89", "printf '<%#.20g>' 0.00001; printf '|%s' \"$?\"", 0, "3C392E39393939393939393939393939393939393939652D30363E7C30", ""],
        ["finite-90", "printf '<%#.20a>' 0.00001; printf '|%s' \"$?\"", 0, "3C3078612E3763356163343731623437383432333030303030702D32303E7C30", ""],
        ["finite-91", "printf '<%#.20f>' 0x1p-16445; printf '|%s' \"$?\"", 0, "3C302E30303030303030303030303030303030303030303E7C30", ""],
        ["finite-92", "printf '<%#.20e>' 0x1p-16445; printf '|%s' \"$?\"", 0, "3C332E3634353139393533313838323437343630323533652D343935313E7C30", ""],
        ["finite-93", "printf '<%#.20g>' 0x1p-16445; printf '|%s' \"$?\"", 0, "3C332E36343531393935333138383234373436303235652D343935313E7C30", ""],
        ["finite-94", "printf '<%#.20a>' 0x1p-16445; printf '|%s' \"$?\"", 0, "3C3078302E3030303030303030303030303030313030303030702D31363338353E7C30", ""],
        ["finite-95", "printf '<%#.20f>' 0xf.fffffffffffffffp+16380; printf '|%s' \"$?\"", 0, "3C313138393733313439353335373233313736353032313236333835333033303937303230353136393036333332323239343632343230303434303332333733333839313733373030353532323937303732323631363431303239303333363532383838323835333534353639373830373439353537373331343432373434333135333637303238383433343139383132353537333835333734333637383637333539333230303730363937333236333230313931353931383238323936313532343336353532393531303634363739313038363631343331313739303633323136393737383833383839363133343738363536303630303339393134383735333433333231313435343931313136303038383637393834353135343836363531323835323334303134393737333033373630303030393132353437393339333936363232333135313338333632323431373833383534323734333931373833383133383731373830353838393438373534303537353136383232363334373635393233353537363937343830353131333732353634393032303838343835353232323439343739313339393337373538353032363031313737333534393138303039393739363232363032363835393530383535383838333630383135393834363930303233353634353133323334363539343437363338343933393835393237363435363238343537393636313737323933303430373830363630393232393130323731353034363038353338383038373935393332373738313632323938363832373534373833303736383038303034303135303639343934323330333431313732383935373737373130303333353731343031303535393737353234323132343035373334373030373338363235313636303131303832383337393131393632333030383436393237373230303936353135333530303230383437343437303739323434333834383534353931323838363732333030303631393038353132363437323131313935313336313436373532373633333531393536323932373539373935373235303237383030323938303739353930343139333133393630333032313437303939373033353237363436373434353533303932323032323637393635363238303939313439383233323038333332393634313234313033383530393233393138343733343738363132313932313639373231303534333438343238373034383335333430383131333034323537333030323231363432313334383931373334373137343233343830303731343838303735313030323036343339303531373233343234373635363030343732313736383039363438363130373939343934333431353730333437363332303634333535383632343230373434333530343432343338303536363133363031373630383833373437383136353338393032373830393537363937353937373238363836303037313438373032383238373935353536373134313430343633323631353833323632333630323736323839363331363137333937383438343235343438363836303630393934383237303836373936383034383037383730323531313835383933303833383534363538343232333034303930383830353939363239343539343538363230313930333736363034383434363739303932363030323232353431303533303737353930313036353736303637313334373230303132353834363430363935373033303235373133383936303938333735373939383932363935343535333035323336383536303735383638333137393232333131333633393531393436383835303838303737313837323130343730353230333935373538373438303031333134333133313434343235343934333931393934303137353735333136393333393339323336363838313835363138393132393933313732393130343235323932313233363833353135393932323332323035303939383030313637373130323738343033353336303134303832393239363339383131353132323837373736383133353730363034353738393334333533353435313639363533393536313235343034383834363434373136393738363839333231313637313038373232393038383038323737383335303531383232383835373634363036323231383733393730323835313635353038333732303939323334393438333333343433353232383938343735313233323735333732363633363036363231333930323238313236343730363233343037353335323037313732343035383636353037393531383231373330333436333738323633313335333339333730363737343930313935303139373834313639303434313832343733383036333136323832383538363835373734313433323538313136353336343034303231383430323732343931333339333332303934393231393439383432323434323733303432373031393837333034343533363632303335303236323338363935373830343638323030333630313434373239313939373132333039353533303035373230363134313836363937343835323834363835363138363531343833323731353937343438313230333132313934363735313638363337393334333039363138393631353130373333303036353535323432313438353139353230313736323835383539353039313035313833393437323530323836333837313633323439343136373631333830343939363331393739313434313837303235343330323730363735383439353139323030383833373931353136393430313538313734303034363731313437373837373230313435393634343436313137353230343035393435333530343736343732313830373937353736313131313732303834363237333633393237393630303333393637303437303033373631333337343530393535333138343135303037333739363431323630353034373932333235313636313335343834313239313838343231313334303832333031353437333330343735343036373037323831383736333530333631373333323930383030353935313839363332353230373037313637333930343534373737373132393638323236353230363232353635313433393931393337363830343430303239323338303930333131323433373931323631343737363235353936343639343232313938313337353134363936373037393434363837303335383030343339323530373635393435313631383337393831313835393339323034393534343033363131343931353331303738323235313037323639313438363937393830393234303934363737323134323732373031323430343337373138373430393231363735363631333633343933383930303435313233323335313636383134363038393332323430303639373939333137363031373830353333383139313834393938313933333030383431303938353939333933383736303239323630313339303931313431343532363030333732303238343837323133323431313935353432343238323130313833313230343231363130343436373430343632313633353333363930303538333636343630363539313135363239383736343734353532353036383134353030333933323934313430343133313439353430303637373630323935313030353936323235333032323832333030333633313437333832343638313035393634383434323434313332343836343537333133373433373539353039363431363136383034383032343132393335313837363230343636383133353633363837373533323831343637353533383739383837313737313833363531323839333934373139353333353036313838353030333236373630373335343338383637333336383030323037343338373834393635373031343537363039303334393835373537313234333034353130323033383733303439343835343235363730323437393333393332323830393131303532363034313533383532383939343834393230333939313039313934363132393931323439313633333238393931373939383039343338303333373837393532323039333133313436363934363134393730353933393636343135323337353934393238353839303936303438393931363132313934343938393938363338343833373032323438363637323234393134383932343637383431303230363138333336343632373431363936393537363330373633323438303233353538373937353234353235333733373033353433333838323936303836323735333432373734303031363333333433343035353038333533373034383530373337343534343831393735343732323232383937353238313038333032303839383638323633333032303238353235393932333038343136383035343533393638373931313431383239373632393938383936343537363438323736353238373530343536323835343932343236353136353231373735303739393531363235393636393232393131343937373738383936323335363637303935363632373133383438323031383139313334383332313638373939353836333635323633373632303937383238353037303039393333373239343339363738343633393837393032343931343531343232323734323532373030363336333934323332373939383438333937363733393938373135343431383535343230313536323234343135343932363635333031343531353530343638353438393235383632303237363038353736313833373132393736333335383736313231353338323536353132393633333533383134313636333934393531363535363030303236343135393138363535343835303035373035323631313433313935323931393931383830373935343532323339343634393632373633353633303137383538303839363639323232363430363233353338323839383533353836373539353939303634373030383338353638373132333831303332393539313932363439343834363235303736383939323235383431393330353438303736333632303231353038393032323134393232303532383036393834323031383335303834303538363933383439333831353439383930393434353436313937373839333032393131333537363531363737353430363233323237383239383331343033333437333237363630333935323233313630333432323832343731373532383138313831383834343330343838303932313332313933333535303836393837333339353836313237363037333637303836363635323337353535353637353830333137313439303130383437373332303039363432343331383738303037303030383739373334363033323930363237383934333535333734333536343434383835313930373139313631363435353134313135353736313933393339393639303736373431353135363430323832363534333636343032363736303039353038373532333934353530373334313535363133353836373933333036363033313734343732303932343434363531333533323336363634373634393733353430303835313936373034303737313130333634303533383135303037333438363839313739383336343034393537303630363138393533353030353038393834303931333832363836393533353039303036363738333332343437323537383731323139363630343431353238343932343834303034313835303933323831313930383936333633343137353733393839373136363539363030303735393438373830303631393136343039343835343333383735383532303635373131363534313037323236303939363238383135303132333134343337373934343030383734393330313934343734343333303738343338383939353730313834323731303030343830383330353031323137373132333536303632323839353037363236393034323835363830303034373731383839333135383038393335383531353539333836333137363635323934383038393033313236373734373032393636323534353131303836313534383935383339353038373739363735353436343133373934343839353936303532373937353230393837343831333833393736323537383539323130353735363238343430313735393334393332343136323134383333393536353335303138393139363831313338393039313834333739353733343730333236393430363334323839303038373830353834363934303335323435333437393339383038303637343237333233363239373838373130303836373137353830323533313536313330323335363036343837383730393235393836353238383431363335303937323532393533373039313131343331373230343838373734373430353533393035343030393432353337353432343131393331373934343137353133373036343638393634333836313531373731383834393836373031303334313533323534323338353931313038393632343731303838353338353830383638383833373737373235383634383536343134353933343236323132313038363634373538383438393236303033313736323334353936303736393530383834393134393636323434343135363630343431393535323038363831313938393737303234302E30303030303030303030303030303030303030303E7C30", ""],
        ["finite-96", "printf '<%#.20e>' 0xf.fffffffffffffffp+16380; printf '|%s' \"$?\"", 0, "3C312E3138393733313439353335373233313736353032652B343933323E7C30", ""],
        ["finite-97", "printf '<%#.20g>' 0xf.fffffffffffffffp+16380; printf '|%s' \"$?\"", 0, "3C312E31383937333134393533353732333137363530652B343933323E7C30", ""],
        ["finite-98", "printf '<%#.20a>' 0xf.fffffffffffffffp+16380; printf '|%s' \"$?\"", 0, "3C3078662E6666666666666666666666666666663030303030702B31363338303E7C30", ""],
    ];

    public static IEnumerable<object[]> Fields() => Cases(boundary: false);
    public static IEnumerable<object[]> BoundaryControls() => Cases(boundary: true);

    private static IEnumerable<object[]> Cases(bool boundary)
    {
        foreach (var prepared in new[] { false, true })
        foreach (var sample in Samples)
            if (((string)sample[0]).Contains("-1048576", StringComparison.Ordinal) == boundary)
                yield return [prepared, ..sample];
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public Task FieldsUseActualOutputInsteadOfAnArbitraryPrecisionLimit(
        bool prepared, string name, string source, int status, string outputHex, string diagnosticHex) =>
        CheckAsync(prepared, name, source, status, outputHex, diagnosticHex);

    [Theory]
    [MemberData(nameof(BoundaryControls))]
    public Task FormerBoundaryPreservesOutputAndEffects(
        bool prepared, string name, string source, int status, string outputHex, string diagnosticHex) =>
        CheckAsync(prepared, name, source, status, outputHex, diagnosticHex);

    private static async Task CheckAsync(
        bool prepared, string name, string source, int status, string outputHex, string diagnosticHex)
    {
        var memory = new MemoryGovernor(64 * 1024 * 1024);
        using var owner = memory.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancel.Token)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancel.Token);
        Assert.True(result.ExitCode == status, name);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(diagnosticHex), host.StandardError.Content.ToArray());
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("%2147483647s")]
    [InlineData("%.2147483647d")]
    [InlineData("%.2147483647f")]
    [InlineData("%.2147483647e")]
    [InlineData("%.2147483647a")]
    [InlineData("%#.2147483647g")]
    public void ActualRepresentationOverflowFailsBeforePadding(string format)
    {
        var memory = new MemoryGovernor(1024 * 1024);
        using var owner = memory.CreateScope();
        var result = PrintfFormatting.EvaluateCommand(ShellValue.FromText(format), [ShellValue.FromText("1")], owner);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output.ToArray());
        Assert.Contains("output exceeds", Assert.Single(result.Diagnostics).ToUtf8String());
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void GeneralPrecisionKeepsTheSameSmallWorkspaceForTheSameResult()
    {
        var ordinary = Format(6);
        var large = Format(int.MaxValue);
        Assert.Equal(ordinary, large);

        long Format(int precision)
        {
            var memory = new MemoryGovernor(1024 * 1024);
            using var owner = memory.CreateScope();
            var result = PrintfFormatting.EvaluateCommand(ShellValue.FromText("%.*g"),
                [ShellValue.FromText(precision.ToString(System.Globalization.CultureInfo.InvariantCulture)), ShellValue.FromText("1.25")], owner);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("1.25"u8.ToArray(), result.Output.ToArray());
            var peak = memory.PeakBytes;
            owner.Dispose();
            Assert.Equal(0, memory.ReservedBytes);
            return peak;
        }
    }

    [Fact]
    public void LargeOutputRetainsCapacityAdmissionUntilItsConsumerFinishes()
    {
        var memory = new MemoryGovernor(8 * 1024 * 1024);
        using var owner = memory.CreateScope();
        var result = PrintfFormatting.EvaluateCommand(ShellValue.FromText("%1048577s"), [ShellValue.FromText("x")], owner);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1048577, result.Output.Length);
        Assert.Equal((byte)'x', result.Output.Span[^1]);
        Assert.True(result.Output.Span[..^1].IndexOfAnyExcept((byte)' ') < 0);
        Assert.True(memory.ReservedBytes >= result.Output.Length);
        Assert.True(memory.PeakBytes > memory.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task PaddingRefusalKeepsEarlierCountEffectsAndSkipsLaterOnes()
    {
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        var counts = new List<PrintfVariableAssignment>();
        var diagnostics = new List<PrintfFormatting.Diagnostic>();
        var result = await PrintfFormatting.EvaluateAsync(ShellValue.FromText("a%n%1048577s%n"),
            [ShellValue.FromText("before"), ShellValue.FromText("x"), ShellValue.FromText("after")], DateTimeOffset.UnixEpoch, owner,
            assignment => { counts.Add(assignment); return ValueTask.FromResult(true); },
            diagnostic => { diagnostics.Add(diagnostic); return ValueTask.FromResult(true); }, CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(new[] { new PrintfVariableAssignment("before", 1) }, counts);
        Assert.Empty(result.Output.ToArray());
        Assert.Equal(PrintfFormatting.Diagnostic.MemoryRefusal, Assert.Single(diagnostics));
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void ReturnedFormatDiagnosticsKeepTheirOwnByteReservation()
    {
        var format = "%" + new string('9', 4096);
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();

        var result = PrintfFormatting.EvaluateCommand(ShellValue.FromText(format), [], owner);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output.ToArray());
        Assert.Equal([format + ": numerical result out of range", format + ": missing format character"],
            result.Diagnostics.Select(diagnostic => diagnostic.ToUtf8String()));
        Assert.True(memory.ReservedBytes >= result.Diagnostics.Sum(diagnostic => diagnostic.Bytes.Length));
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public void OutputOnlyFormattingFitsWithoutMaterializingDiscardedDiagnostics()
    {
        var invalidNumber = ShellValue.FromText("1" + new string('x', 4096));
        var memory = new MemoryGovernor(4096);
        using var owner = memory.CreateScope();

        var result = PrintfFormatting.Format(ShellValue.FromText("%d"), [invalidNumber], owner);

        Assert.Equal("1"u8.ToArray(), result.ToArray());
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task PaddingGrowthObservesCancellationBeforeTheNextCountWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new MemoryGovernor(8 * 1024 * 1024);
        var budget = new CountingMemoryBudget(memory);
        using var owner = budget.CreateScope();
        budget.AdmissionSucceeded += () =>
        {
            if (memory.ReservedBytes >= 128 * 1024) cancellation.Cancel();
        };
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PrintfFormatting.EvaluateAsync(ShellValue.FromText("%1048577s%n"),
                [ShellValue.FromText("x"), ShellValue.FromText("after")], DateTimeOffset.UnixEpoch, owner,
                _ => throw new InvalidOperationException("Unreached count."),
                _ => throw new InvalidOperationException("Unreached diagnostic."), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }
}
