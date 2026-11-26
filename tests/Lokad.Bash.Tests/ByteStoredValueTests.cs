using System.Runtime.InteropServices;
using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteStoredValueTests
{
    [Fact]
    public void SingleByteValuesReuseCompactBackingForEveryByte()
    {
        for (var index = 0; index <= byte.MaxValue; index++)
        {
            var value = ShellValue.FromByte((byte)index);
            var repeated = ShellValue.FromByte((byte)index);

            Assert.Equal(1, value.Bytes.Length);
            Assert.Equal((byte)index, value.Bytes.Span[0]);
            Assert.True(MemoryMarshal.TryGetArray(value.Bytes, out var backing));
            Assert.True(MemoryMarshal.TryGetArray(repeated.Bytes, out var repeatedBacking));
            Assert.Same(backing.Array, repeatedBacking.Array);
            Assert.True(value.TryGetCompactArrayCapacity(out var capacity));
            Assert.Equal(1, capacity);
        }
    }

    [Theory]
    [InlineData("plain", false)]
    [InlineData("plain", true)]
    [InlineData("quoted", false)]
    [InlineData("quoted", true)]
    [InlineData("ansi", false)]
    [InlineData("ansi", true)]
    [InlineData("escaped", false)]
    [InlineData("escaped", true)]
    [InlineData("joined", false)]
    [InlineData("joined", true)]
    [InlineData("parameter", false)]
    [InlineData("parameter", true)]
    [InlineData("long-name", false)]
    [InlineData("long-name", true)]
    public async Task StoredValuesDoNotRetainOversizedParserOrExpansionBuffers(string kind, bool eval)
    {
        var name = kind == "long-name" ? "v" + new string('a', 512) : "value";
        var operand = kind switch
        {
            "quoted" => "\"x\"",
            "ansi" => "$'\\170'",
            "escaped" => "\\x",
            "joined" => "\\x\\y",
            "parameter" => "\"${missing:-x}\"",
            _ => "x"
        };
        var body = name + "=" + operand;
        var source = eval ? "eval \"" + body.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal) + "\"" : "source /unit";
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        host.AddFile("/unit", body);
        var evaluation = new Evaluation.Evaluation(Parser.Parse(source));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        var value = evaluation.Variables[name];
        Assert.Equal(kind == "joined" ? "xy" : "x", value.ToUtf8String());
        Assert.True(MemoryMarshal.TryGetArray(value.Bytes, out var storage));
        var array = Assert.IsType<byte[]>(storage.Array);
        Assert.InRange(array.Length, value.Bytes.Length, 32 + 2 * value.Bytes.Length);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    // Bash 5.2.21, LC_ALL=C.UTF-8; compare bytes without a text-decoding test adapter.
    [Theory]
    [InlineData("x=$'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("a=($'\\377' $'\\303' $'\\251'); printf '<%s>' \"${a[@]}\"\n", "3CFF3E3CC33E3CA93E")]
    [InlineData("a[3]=$'\\377'; printf '<%s>' \"${a[3]}\"\n", "3CFF3E")]
    [InlineData("a=($'\\377'); printf '<%s>' \"$a\" \"${a[0]}\"", "3CFF3E3CFF3E")]
    [InlineData("declare -A a; a=(0 $'\\377'); printf '<%s>' \"$a\" \"${a[0]}\"", "3CFF3E3CFF3E")]
    [InlineData("a=([1]=keep); printf '<%s>' \"${a:=$'\\377'}\" \"${a[0]}\" \"${a[1]}\"", "3CFF3E3CFF3E3C6B6565703E")]
    [InlineData("x=$'\\376'; f(){ declare x=$'\\377'; printf '<%s>' \"$x\"; }; f; printf '<%s>' \"$x\"", "3CFF3E3CFE3E")]
    [InlineData("f(){ printf '<%s>' \"$X\"; }; X=($'\\377' $'\\376') f", "3C28FF20FE293E")]
    [InlineData("x=$'\\376'; f(){ local x; local x=$'\\377'; printf '<%s>' \"$x\"; }; f; printf '<%s>' \"$x\"", "3CFF3E3CFE3E")]
    [InlineData("declare -A a; a[k]=$'\\377'; printf '<%s>' \"${a[k]}\"\n", "3CFF3E")]
    [InlineData("printf '<%s>' \"${missing:-$'\\377'}\"\n", "3CFF3E")]
    [InlineData("printf '<%s>' \"${x:=$'\\377'}\" \"$x\"\n", "3CFF3E3CFF3E")]
    [InlineData("x=$'\\377'; printf '<%s>' \"${x:+$'\\376'}\"\n", "3CFE3E")]
    [InlineData("x=$'\\377'; y=\"${x:-no}\"; printf '<%s>' \"$y\"\n", "3CFF3E")]
    [InlineData("export x=$'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("readonly x=$'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("declare x=$'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("x=$'\\376'; f(){ local x=$'\\377'; printf '<%s>' \"$x\"; }; f; printf '<%s>' \"$x\"\n", "3CFF3E3CFE3E")]
    [InlineData("i=0; declare x=$((i++)); printf '<%s:%s>' \"$i\" \"$x\"\n", "3C313A303E")]
    [InlineData("set -- $'\\377' $'\\376'; printf '<%s>' \"$1\" \"$2\"\n", "3CFF3E3CFE3E")]
    [InlineData("set -- $'\\376'; f(){ printf '<%s>' \"$1\"; }; f $'\\377'; printf '<%s>' \"$1\"\n", "3CFF3E3CFE3E")]
    [InlineData("set -- $'\\377' $'\\376'; shift; printf '<%s>' \"$1\"\n", "3CFE3E")]
    [InlineData("for x in $'\\377' $'\\376'; do printf '<%s>' \"$x\"; done\n", "3CFF3E3CFE3E")]
    [InlineData("x=$'\\377'; ( x=$'\\376'; printf '<%s>' \"$x\" ); printf '<%s>' \"$x\"\n", "3CFE3E3CFF3E")]
    [InlineData("x=$(printf '\\xff\\n\\n'); printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("printf -v x '\\xff\\0tail'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("printf -v 'a[3]' '\\xff'; printf '<%s>' \"${a[3]}\"\n", "3CFF3E")]
    [InlineData("IFS=$'\\377'; x=$'a\\377b'; printf '<%s>' $x\n", "3C613E3C623E")]
    [InlineData("IFS=$'\\377'; set -- a b; printf '<%s>' \"$*\"\n", "3C61FF623E")]
    [InlineData("IFS=$'\\377'; set -- a b; printf '<%s>' $*\n", "3C613E3C623E")]
    [InlineData("IFS=é; x=$'a\\303\\251b'; printf '<%s>' $x\n", "3C613E3C623E")]
    [InlineData("IFS=$'\\377'; x=é; printf '<%s>' $x\n", "3CC3A93E")]
    [InlineData("IFS=$'\\377'; set -- '' b ''; printf '<%s>' \"$*\"\n", "3CFF62FF3E")]
    [InlineData("IFS=; set -- $'\\303' $'\\251'; printf '<%s>' \"$*\"\n", "3CC3A93E")]
    [InlineData("HOME=$'/\\377'; printf '<%s>' ~\n", "3C2FFF3E")]
    [InlineData("IFS= read -r x <<< $'\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("IFS= read x <<< $'\\\\\\377'; printf '<%s>' \"$x\"\n", "3CFF3E")]
    [InlineData("IFS= read -r x <<< $'\\377\\r'; printf '<%s>' \"$x\"\n", "3CFF0D3E")]
    [InlineData("IFS=$'\\377' read -r a b <<< $'a\\377b'; printf '<%s>' \"$a\" \"$b\"\n", "3C613E3C623E")]
    [InlineData("IFS=é read -r a b <<< $'a\\303\\251b'; printf '<%s>' \"$a\" \"$b\"\n", "3C613E3C623E")]
    [InlineData("IFS=é read -r a b <<< $'a\\303b'; printf '<%s>' \"$a\" \"$b\"\n", "3C613E3C623E")]
    [InlineData("IFS=$'\\377' read a b <<< $'a\\\\\\377b\\377c'; printf '<%s>' \"$a\" \"$b\"\n", "3C61FF623E3C633E")]
    [InlineData("IFS=$'\\377' read a b <<< $'a\\377b\\377c'; printf '<%s>' \"$a\" \"$b\"\n", "3C613E3C62FF633E")]
    [InlineData("IFS= read x <<'EOF'\na\\\nÿ\nEOF\nprintf '<%s>' \"$x\"\n", "3C61C3BF3E")]
    [InlineData("cat <<< $'\\377'\n", "FF0A")]
    [InlineData("cat <<< $'\\377\\n'\n", "FF0A0A")]
    [InlineData("x=$'\\377'; cat <<EOF\n$x\nEOF\n", "FF0A")]
    [InlineData("getopts a opt -a; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3C613E")]
    [InlineData("getopts é opt -é; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3CC33E")]
    [InlineData("set -- -é; getopts é opt; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3CC33E")]
    [InlineData("getopts $'\\200' opt $'-\\200'; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3C803E")]
    [InlineData("set -- $'-\\200'; getopts $'\\200' opt; printf '%s:<%s>' \"$?\" \"$opt\"", "303A3C803E")]
    [InlineData("getopts a: opt $'-a\\377\\376'; printf '<%s:%s>' \"$opt\" \"$OPTARG\"", "3C613AFFFE3E")]
    [InlineData("getopts :a opt $'-\\377a'; printf '<%s:%s>' \"$opt\" \"$OPTARG\"", "3C3F3AFF3E")]
    [InlineData("p=18446744073709551617; ((p)); printf '%s' \"$?\"; let p; printf '%s' \"$?\"; "
        + "printf '%s' \"$((p))\"; for ((i=p;i<2;i++)); do printf '%s' \"$i\"; done", "30303131")]
    [InlineData("x=$'\\376'; f(){ y=$x; x=$'\\375'; }; x=$'\\377' f; printf '<%s>' \"$x\" \"$y\"", "3CFE3E3CFF3E")]
    [InlineData("a=($'\\376' $'\\375'); a=temp printf -v a '\\xff\\0tail'; printf '<%s>' \"${a[@]}\"", "3CFF3E3CFD3E")]
    [InlineData("a=($'\\376' $'\\375'); f(){ declare -n ref=a; a=temp printf -v ref '\\xff'; }; f; printf '<%s>' \"${a[@]}\"", "3CFF3E3CFD3E")]
    [InlineData("x=$'\\376'; declare -n ref=x; x=temp printf -v ref '\\xff'; printf '<%s>' \"$x\"", "3CFE3E")]
    [InlineData("x=$'\\376'; f(){ y=temp eval 'export x'; }; x=$'\\377' f; printf '<%s>' \"$x\"", "3CFF3E")]
    [InlineData("x=$'\\376'; f(){ y=temp eval 'readonly x'; }; x=$'\\377' f; printf '<%s>' \"$x\"", "3CFF3E")]
    [InlineData("opt=($'\\376' $'\\375'); opt=temp getopts $'\\200' opt $'-\\200'; printf '<%s>' \"${opt[@]}\"", "3C803E3CFD3E")]
    [InlineData("a=(8 $'\\377'); a=1 let 'a=a+1' 'a=a+1'; printf '<%s>' \"${a[@]}\"", "3C333E3CFF3E")]
    [InlineData("set -- $'\\377' $'\\376'; printf '<%s>' \"${x:-\"$@\"}\"", "3CFF3E3CFE3E")]
    [InlineData("set -- $'\\377' $'\\376'; printf '<%s>' \"${x:=\"$@\"}\" \"$x\"", "3CFF20FE3E3CFF20FE3E")]
    [InlineData("set -- $'\\377' $'\\376'; IFS=:; printf '<%s>' ${x:-\"$*\"}", "3CFF3AFE3E")]
    [InlineData("set -- $'\\377' $'\\376'; x=pre${value:-\"$@\"}post; printf '<%s>' \"$x\"", "3C707265FF20FE706F73743E")]
    [InlineData("set -- '' $'\\377' ''; x=${value:-${inner:-$@}}; printf '<%s>' \"$x\"", "3C20FF203E")]
    [InlineData("set -- $'\\377' $'\\376'; cat <<EOF\n${value:-\"$@\"}\ntail\nEOF\n", "FF20FE0A7461696C0A")]
    public async Task Evaluation_PreservesStoredBytes(string script, string outputHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task Source_RetainsByteArgumentsAndRestoresCallerPositionals()
    {
        var tree = EndToEndHarness.BuildTree([("/script", "printf '<%s>' \"$1\"")]);
        var result = await EndToEndHarness.RunScriptAsync(
            "set -- $'\\376'; source /script $'\\377'; printf '<%s>' \"$1\"", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString("3CFF3E3CFE3E"), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData(false, "", "")]
    [InlineData(false, "a", "61")]
    [InlineData(true, "", "")]
    [InlineData(true, "\\xff", "FF")]
    public async Task Printf_TruncatedAssignmentsDoNotRetainDiscardedOutput(bool arrayTarget, string prefix, string expectedHex)
    {
        var target = arrayTarget ? "values[0]" : "value";
        var evaluation = new Evaluation.Evaluation(Parser.Parse(
            $"printf -v '{target}' '{prefix}\\0%524288s' ''"));

        Assert.Null(await evaluation.GetNextStep());
        Assert.Equal(0, evaluation.LastExitCode);
        var stored = arrayTarget ? evaluation.IndexedArrays["values"][0] : evaluation.Variables["value"];
        Assert.Equal(Convert.FromHexString(expectedHex), stored.Bytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(stored.Bytes, out var storage));
        Assert.NotNull(storage.Array);
        Assert.True(storage.Array.Length <= 16, $"Retained {storage.Array.Length} bytes for a truncated value.");
    }

    [Fact]
    public async Task Printf_AssignmentCompactsALargePrefixBeforeNul()
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse(
            "printf -v value 'a%65536s\\0tail' z"));

        Assert.Null(await evaluation.GetNextStep());
        Assert.Equal(0, evaluation.LastExitCode);
        var stored = evaluation.Variables["value"];
        Assert.Equal(65_537, stored.Bytes.Length);
        Assert.Equal((byte)'a', stored.Bytes.Span[0]);
        Assert.Equal((byte)'z', stored.Bytes.Span[^1]);
        Assert.True(MemoryMarshal.TryGetArray(stored.Bytes, out var storage));
        Assert.Equal(stored.Bytes.Length, storage.Array?.Length);
    }

    [Fact]
    public async Task Printf_AssignmentCountsWidePaddingAfterNulWithinPrefixBudget()
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse(
            "printf -v value 'a%n\\0%524288s%n' before x after"));
        var memory = new MemoryGovernor(64 * 1024);
        using var owner = memory.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ShellValue.FromText("a"), evaluation.Variables["value"]);
        Assert.Equal(ShellValue.FromText("1"), evaluation.Variables["before"]);
        Assert.Equal(ShellValue.FromText("524290"), evaluation.Variables["after"]);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false, "5C78", "78")]
    [InlineData(true, "5C78", "78")]
    [InlineData(false, "5CC3A9", "C3A9")]
    [InlineData(true, "5CC3A9", "C3A9")]
    [InlineData(false, "5CF09F9982", "F09F9982")]
    [InlineData(true, "5CF09F9982", "F09F9982")]
    [InlineData(false, "5CFF", "FF")]
    [InlineData(true, "5CFF", "FF")]
    [InlineData(false, "5C0D", "0D")]
    [InlineData(true, "5C0D", "0D")]
    [InlineData(false, "225C2422", "24")]
    [InlineData(true, "225C2422", "24")]
    [InlineData(false, "225C6022", "60")]
    [InlineData(true, "225C6022", "60")]
    [InlineData(false, "225C2222", "22")]
    [InlineData(true, "225C2222", "22")]
    [InlineData(false, "225C5C22", "5C")]
    [InlineData(true, "225C5C22", "5C")]
    [InlineData(false, "225C7822", "5C78")]
    [InlineData(true, "225C7822", "5C78")]
    public async Task DynamicEscapedValuesRetainAdmittedStorage(bool eval, string wordHex, string expectedHex)
    {
        // The retained value must not pin the unrelated comment after the source owner ends.
        var source = ByteMemoryBuilder.CreateUnaccounted(0)
            .Append("value="u8).Append(Convert.FromHexString(wordHex))
            .Append("\n#"u8).Append(Utf8Text.Encode(new string('a', 8192))).Append((byte)'\n')
            .ToMemory();
        await using var host = new TestHost();
        host.AddFile("/unit", source.ToArray());
        var memory = new MemoryGovernor(65536);
        host.MemoryBudget = memory;
        using var owner = memory.CreateScope();
        var script = eval ? ByteMemoryBuilder.CreateUnaccounted(0)
            .Append("eval '"u8).Append(source).Append((byte)'\'').ToMemory() : "source /unit"u8.ToArray();
        var evaluation = new Evaluation.Evaluation(Parser.Parse(script));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, cancel.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        var stored = evaluation.Variables["value"];
        Assert.Equal(Convert.FromHexString(expectedHex), stored.Bytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(stored.Bytes, out var storage));
        Assert.NotNull(storage.Array);
        Assert.InRange(storage.Array.LongLength, stored.Bytes.Length, memory.ReservedBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicEscapedValuesShareCompactBackingAfterSourceRetirement(bool eval)
    {
        const int Count = 64;
        await using var host = new TestHost();
        var memory = new MemoryGovernor(65536);
        host.MemoryBudget = memory;
        using var owner = memory.CreateScope();
        var script = new StringBuilder();
        for (var index = 0; index < Count; index++)
        {
            var source = $"v{index}=\\x\n#" + new string('a', 8192) + "\n";
            host.AddFile($"/unit{index}", source);
            script.Append(eval ? $"eval '{source}'; " : $"source /unit{index}; ");
        }
        var evaluation = new Evaluation.Evaluation(Parser.Parse(script.ToString()));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, cancel.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        var backing = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < Count; index++)
        {
            var value = evaluation.Variables[$"v{index}"];
            Assert.Equal("x"u8.ToArray(), value.Bytes.ToArray());
            Assert.True(MemoryMarshal.TryGetArray(value.Bytes, out var storage));
            Assert.NotNull(storage.Array);
            backing.Add(storage.Array);
        }
        var shared = Assert.Single(backing);
        Assert.Equal("x"u8.ToArray(), shared);
        Assert.InRange(shared.LongLength, 1, memory.ReservedBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("5C000A", "", "next")]
    [InlineData("5C000A", "-r", "next")]
    [InlineData("5C005C0A", "", "")]
    [InlineData("5C005C0A", "-r", "next")]
    [InlineData("5C5C000A", "", "next")]
    [InlineData("5C0A", "", "")]
    [InlineData("5C0A", "-r", "next")]
    public async Task Read_ContinuationUsesTheUnfilteredPhysicalLine(string firstLineHex, string options, string expectedNext)
    {
        // Compare line consumption with Bash, not its escaped-NUL truncation/sentinel quirks.
        var input = Convert.FromHexString(firstLineHex + "6E6578740A");
        var result = await EndToEndHarness.RunScriptAsync(
            $"IFS= read {options} first; IFS= read {options} next; printf '%s' \"$next\"",
            EndToEndHarness.BuildTree([]), input);

        Assert.Equal(expectedNext, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("0A", "303A307C")]
    [InlineData("5C000A", "303A317C")]
    [InlineData("635C5C5CC3A95CFF010A", "303A317C635CC3A9FF01")]
    public async Task ReadArrayWithEmptyIfsDistinguishesEmptyRecordsAndEscapedValues(string inputHex, string outputHex)
    {
        const string script = "values=(old); IFS= read -a values; " +
            "printf '%s:%s|%s' \"$?\" \"${#values[@]}\" \"${values[0]-}\"";
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(script, host, Convert.FromHexString(inputHex));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16384)]
    public void ReadFieldEndingAtAQuotedNulBorrowsItsUnchangedPrefix(int length)
    {
        var governor = new MemoryGovernor(0);
        using var owner = governor.CreateScope();
        var record = new byte[length + 2];
        record.AsSpan(0, length).Fill(0xff);
        record[length] = (byte)'\\';
        var prefix = record.AsMemory(0, length);

        using (var plain = new MemoryCapacity(owner, 0))
        using (var truncated = new MemoryCapacity(owner, 0))
        {
            var control = ReadFieldSplitter.DecodeField(prefix, ReadBackslashMode.InterpretEscapes,
                plain, CancellationToken.None);
            var field = ReadFieldSplitter.DecodeField(record, ReadBackslashMode.InterpretEscapes,
                truncated, CancellationToken.None);

            Assert.Equal(control, field);
            Assert.True(field.Bytes.Equals(length == 0 ? ShellValue.Empty.Bytes : prefix));
        }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ReadDetachesItsTruncatedFieldBeforeReusingTheRecord()
    {
        byte[] input = [.. "first"u8, 0xff, (byte)'\\', 0, .. "discarded\nsecond value\n"u8];
        const string script = "IFS= read first; IFS= read second; printf '<%s><%s>' \"$first\" \"$second\"";
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(script, host, input);

        Assert.Equal(0, result.ExitCode);
        byte[] expected = [.. "<first"u8, 0xff, .. "><second value>"u8];
        Assert.Equal(expected, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("unset IFS", "read -r first last; printf '<%s><%s>' \"$first\" \"$last\"", "2009C3A9FF09612020620D20090A", "3CC3A9FF3E3C612020620D3E")]
    [InlineData("unset IFS", "read -ra values; printf '<%s>' \"${values[@]}\"", "09C3A9FF2061625C63200A", "3CC3A9FF3E3C61625C633E")]
    [InlineData("unset IFS", "read -ra values; printf '<%s>' \"${#values[@]}\"", "2009200A", "3C303E")]
    [InlineData("IFS=':'", "read -ra values; printf '<%s>' \"${values[@]}\"", "3A3A613AC3A93A3A2063203A0A", "3C3E3C3E3C613E3CC3A93E3C3E3C2063203E")]
    [InlineData("IFS=$' \\t:'", "read -r first last; printf '<%s><%s>' \"$first\" \"$last\"", "20203A093AC3A9203A206320203A090A", "3C3E3C3AC3A9203A206320203A3E")]
    [InlineData("IFS=' :'", "read -r first last; printf '<%s><%s>' \"$first\" \"$last\"", "203AC3A9FF203A2078FE20200A", "3C3E3CC3A9FF203A2078FE3E")]
    [InlineData("IFS=$' \\t:'", "read first last; printf '<%s><%s>' \"$first\" \"$last\"", "205CC3A95CFF5C3A203A0961395C2020625C20090A", "3CC3A9FF3A3E3C61392020623E")]
    [InlineData("IFS=$' \\\\\\:'", "read -a values; printf '<%s>' \"${values[@]}\"", "2061205C3A205C5C3A5C003A7A0A", "3C613E3C3A3E3C5C3E3C3E")]
    public async Task ReadWithShortAsciiIfsPreservesRawBytesAndFields(string setup, string command, string inputHex, string outputHex)
    {
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(setup + "; " + command, host, Convert.FromHexString(inputHex));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("é:")]
    [InlineData("©:")]
    public async Task ReadArrayStoresTheEscapedLeadByteBeforeAnIfsContinuation(string separators)
    {
        await using var host = new TestHost();
        host.SetStandardInputBytes([.. "x:\\é:y\n"u8]);
        var evaluation = new Evaluation.Evaluation(Parser.Parse(
            $"IFS='{separators}'; read -a values && test -v 'values[2]'"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        var stored = evaluation.IndexedArrays["values"];
        Assert.Equal(4, stored.Count);
        Assert.Equal("x"u8.ToArray(), stored[0].Bytes.ToArray());
        Assert.Equal(new byte[] { 0xc3 }, stored[1].Bytes.ToArray());
        Assert.True(stored[2].IsEmpty);
        Assert.Equal("y"u8.ToArray(), stored[3].Bytes.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("é:", "", "3C783E3CC33E3C3E3C793E")]
    [InlineData("ñ:", "", "3C783E3CC3A93E3C793E3C3E")]
    [InlineData("©:", "-r", "3C783E3C5CC3A93E3C793E3C3E")]
    public async Task ReadNamedFieldsPreserveByteEscapeAndScalarBoundaries(string separators, string option, string outputHex)
    {
        var script = $"IFS='{separators}'; read {option} first second third fourth; " +
            "printf '<%s>' \"$first\" \"$second\" \"$third\" \"$fourth\"";
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync(script, host, "x:\\é:y\n"u8.ToArray());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ReadFitsWithoutAnAsciiMembershipIndex(bool raw, bool retainEscape)
    {
        var governor = new MemoryGovernor(retainEscape ? 384 : 256);
        using var owner = governor.CreateScope();
        var memory = new EvaluationMemory(owner);
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(" \t:")));
        using var content = new ByteMemoryBuilder(0, owner);
        var input = new ReadInputBuffer(content, raw
            ? ReadBackslashMode.Preserve : ReadBackslashMode.InterpretEscapes);
        var firstLine = raw ? " first \t:second:third \t "u8 : " first \t:second:third \t \\"u8;
        if (retainEscape)
            firstLine = " f\\irst \t:second:third \t \\"u8;
        Assert.Equal(!raw, input.AppendLine(firstLine, hasTerminator: true, CancellationToken.None));
        if (!raw)
            Assert.False(input.AppendLine([], hasTerminator: true, CancellationToken.None));
        Assert.Equal(retainEscape ? ReadBackslashMode.InterpretEscapes : ReadBackslashMode.Preserve,
            input.FieldBackslashMode);
        using (var scratch = new MemoryCapacity(owner, 0))
        {
            var options = new ReadBuiltinOptions(FieldSeparators.FromVariables(variables),
                input.FieldBackslashMode, ReadFieldMode.Split);
            var fields = ReadFieldSplitter.Split(content.ToMemory(), 2,
                options, scratch, variables, memory, CancellationToken.None);

            Assert.Equal([ShellValue.FromText("first"), ShellValue.FromText("second:third")], fields);
            Assert.Equal(0, memory.Workspace.Bytes);
        }
        memory.Finish(retainVariables: false);
        content.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void AssociativeAssignment_RejectsEmptyKeysWithoutReplacingExistingValues()
    {
        var variables = Variables.CreateEmpty();
        var original = ShellValue.FromText("original");
        Assert.True(variables.TrySetScalar("value", original));

        Assert.False(variables.TryAssignArray("value",
            [ShellValue.Empty, ShellValue.Empty], ArrayKind.Associative));

        Assert.True(variables.TryGetScalar("value", out var retained));
        Assert.Equal(original, retained);
        Assert.Empty(variables.AssociativeArrays);
    }

    [Fact]
    public async Task Invocation_ExportsByteValuesWithoutDecoding()
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse("X=$'\\377' inspect"));
        var operation = Assert.IsType<RunCommand>(await evaluation.GetNextStep());
        var command = operation.Command;
        var value = Assert.Single(command.Environment, variable => variable.Name == "X").Value;

        Assert.Equal(new byte[] { 0xff }, value.Bytes.ToArray());
        operation.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await evaluation.GetNextStep());
        Assert.False(evaluation.Variables.ContainsKey("X"));
    }

    [Fact]
    public void Clone_DetachesBindingsWhileRetainingImmutableByteStorage()
    {
        var value = ShellValue.FromBytes(new byte[] { 0xff, 0xc3 });
        var variables = Variables.CreateInitial(
            new Dictionary<BindingName, ShellValue> { ["x"] = value }, [value]);
        Assert.True(variables.TrySetArrayElement("a", ShellValue.FromText("3"), value, ArrayKind.Indexed));
        variables.SetExported("x", true);

        var clone = variables.Clone();
        Assert.True(clone.TryGetScalar("x", out var scalar));
        Assert.True(value.Bytes.Equals(scalar.Bytes));
        Assert.True(value.Bytes.Equals(clone.PositionalParameters[0].Bytes));
        Assert.True(value.Bytes.Equals(Assert.Single(clone.BuildExportedEnvironment()).Value.Bytes));
        Assert.True(IndexedArrayIndex.TryCreate(3, out var index));
        Assert.True(clone.IndexedArrays["a"].TryGet(index, out var arrayValue));
        Assert.True(value.Bytes.Equals(arrayValue.Bytes));

        Assert.True(clone.TrySetScalar("x", ShellValue.Empty));
        Assert.True(variables.TryGetScalar("x", out var original));
        Assert.Equal(value, original);
    }

    [Fact]
    public void Clone_CopiesOnlyActivePositionalEntries()
    {
        var bytes = ShellValue.FromBytes(new byte[] { 0xff, 0xc3 });
        var last = ShellValue.FromText("tail");
        ShellValue[] active = [bytes, ShellValue.Empty, last];
        var variables = Variables.CreateInitial(new Dictionary<BindingName, ShellValue>(),
            [ShellValue.FromText(new string('x', 4096)), .. active]);
        Assert.True(variables.TryShiftPositionalParameters(1));

        var clone = variables.Clone();
        var compact = Variables.CreateInitial(new Dictionary<BindingName, ShellValue>(), active);

        Assert.Equal(active, clone.PositionalParameters);
        Assert.True(bytes.Bytes.Equals(clone.PositionalParameters[0].Bytes));
        Assert.Equal(compact.PositionalMemoryBytes, clone.PositionalMemoryBytes);
        Assert.True(clone.PositionalMemoryBytes < variables.PositionalMemoryBytes);
        Assert.True(clone.TryShiftPositionalParameters(2));
        Assert.Equal(last, Assert.Single(clone.PositionalParameters));
        Assert.Equal(active, variables.PositionalParameters);
    }

    [Fact]
    public void EmptyPositionalFramesSurviveIndependentReplacementAndRestoration()
    {
        var variables = Variables.CreateEmpty();
        var clone = variables.Clone();
        var live = variables.PositionalParameters;
        var value = ShellValue.FromText("local");

        using (variables.EnterFunctionPositionals([value]))
        {
            Assert.Equal(value, Assert.Single(live));
            Assert.True(clone.TryShiftPositionalParameters(0));
            Assert.Empty(clone.PositionalParameters);
            Assert.True(variables.TryShiftPositionalParameters(1));
        }

        clone.ReplacePositionalParameters([value]);
        Assert.Empty(live);
        Assert.Equal(0, variables.PositionalMemoryBytes);
        Assert.Equal(value, Assert.Single(clone.PositionalParameters));
    }
}
