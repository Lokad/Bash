using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class CommandSubstitutionSyntaxTests
{
    [Fact]
    public void TokenizerConsumesTheLookaheadObjectWithoutKeepingItsCacheEntry()
    {
        var source = ShellSource.FromText("$(printf retained)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out var end, out var first));
        var retained = Assert.IsType<CommandSubstitutionSyntax.Parsed>(first);
        Assert.Equal("printf retained", retained.Source.ToUtf8String());
        Assert.NotEmpty(retained.Script.Commands);
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out var lookedAgain));
        Assert.Same(first, lookedAgain);

        var tokenizer = new Tokenizer(source, 0, context);
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(token.Word.Parts));
        Assert.Same(first, part.Substitution);
        Assert.Equal(end + 1, tokenizer.Position);
        Assert.False(context.TryGetCommandSubstitution(source, 0, out _));
    }

    [Fact]
    public void LookaheadKeysUseSourceStorageAndPosition()
    {
        var source = ShellSource.FromText("$(printf one)$(printf two)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out var end, out var first));
        Assert.False(context.TryGetCommandSubstitution(ShellSource.FromText(source.ToUtf8String()), 0, out _));
        Assert.False(context.TryGetCommandSubstitution(source, end + 1, out _));
        Assert.True(context.TryTakeCommandSubstitution(source, 0, out var cached));
        Assert.Same(first, cached.Substitution);
        Assert.False(context.TryTakeCommandSubstitution(source, 0, out _));
    }

    [Fact]
    public void CachedBodiesDoNotBypassAnExhaustedNestingBudget()
    {
        var source = ShellSource.FromText("$(printf retained)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out var first));
        var depth = 0;
        while (context.TryEnter()) depth++;
        try
        {
            Assert.Equal(ShellScanStatus.Invalid,
                CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out var rejected));
            Assert.Null(rejected);
        }
        finally
        {
            for (var level = 0; level < depth; level++) context.Exit();
        }
        Assert.True(context.TryTakeCommandSubstitution(source, 0, out var cached));
        Assert.Same(first, cached.Substitution);
    }

    [Fact]
    public void CachedBodiesDoNotBypassCommandNestingLimits()
    {
        var source = ShellSource.FromText("$(printf retained)");
        var context = new ShellScanContext();
        Assert.Equal(ShellScanStatus.Complete,
            CommandSubstitutionScanner.ScanParsedForLaterReuse(context, source, 0, out _, out _));
        for (var depth = 0; depth < ShellScanContext.MaximumCommandDepth; depth++)
            Assert.True(context.TryEnterCommand());

        Assert.False(context.TryGetCommandSubstitution(source, 0, out _));
        Assert.Equal(ShellScanStatus.Invalid,
            CommandSubstitutionScanner.ScanParsed(context, source, 0, out _, out var rejected));
        Assert.Null(rejected);
        var error = Assert.IsType<SyntaxError>(context.LimitError);
        Assert.Contains("Command nesting", error.Message);
    }

    [Fact]
    public void RetainedTreeContainsCompletedHereDocumentBodies()
    {
        var word = Tokenizer.ReadExpansionWord(ShellSource.FromText("$(cat <<'END'\nbody\nEND\n)"), ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(word.Parts));
        var parsed = Assert.IsType<CommandSubstitutionSyntax.Parsed>(part.Substitution);
        var command = Assert.IsType<SimpleCommand>(Assert.Single(parsed.Script.Commands));
        var redirection = Assert.Single(command.Redirections);
        Assert.Equal("body\n", Assert.Single(redirection.Target.Parts).GetUtf8Text());
        Assert.NotNull(redirection.HereDocumentDelimiter);
        Assert.Equal("END", Assert.Single(redirection.HereDocumentDelimiter.Parts).GetUtf8Text());
    }

    [Theory]
    [InlineData("`if`", "if")]
    [InlineData("$(cat <<END)", "cat <<END")]
    public void DeferredSyntaxKeepsItsExecutionTimeFailure(string source, string payload)
    {
        var word = Tokenizer.ReadExpansionWord(ShellSource.FromText(source), ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(word.Parts));
        var deferred = Assert.IsType<CommandSubstitutionSyntax.Deferred>(part.Substitution);
        Assert.Equal(payload, deferred.Source.ToUtf8String());
        Assert.Throws<SyntaxException>(() => Parser.Parse(deferred.Source.Bytes));
    }

    [Fact]
    public void RetainedBodyPreservesInvalidUtf8LiteralBytes()
    {
        var source = ShellSource.FromBytes("$(printf '"u8.ToArray().Concat(new byte[] { 0xff }).Concat("')"u8.ToArray()).ToArray());
        var word = Tokenizer.ReadExpansionWord(source, ExpansionWordSyntax.Parameter, ShellQuoteKind.None);
        var part = Assert.IsType<CommandSubstitutionWordPart>(Assert.Single(word.Parts));
        var parsed = Assert.IsType<CommandSubstitutionSyntax.Parsed>(part.Substitution);
        var command = Assert.IsType<SimpleCommand>(Assert.Single(parsed.Script.Commands));
        Assert.Equal(new byte[] { 0xff }, Assert.Single(command.Words[1].Parts).GetUnexpandedValue().Bytes.ToArray());
        Assert.True(parsed.Source.Bytes.Equals(source.Bytes.Slice(2, source.Length - 3)));
    }
}
