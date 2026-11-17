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
            ["time-width-unknown-controls", "printf '<%(%3Y)T><%(%J)T><%(%_J)T>' 0 0 0; printf '|%s|tail' \"$?\"\n", "3C313937303E3C254A3E3C255F4A3E7C307C7461696C", false],
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
    public async Task TimeFormatsPreserveBytesFieldsCountsAndStatus(
        bool prepared, string name, string source, string outputHex, bool redirected)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);
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
                DateTimeOffset.UnixEpoch, owner,
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
