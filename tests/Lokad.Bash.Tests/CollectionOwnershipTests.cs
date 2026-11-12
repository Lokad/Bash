using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class CollectionOwnershipTests
{
    [Fact]
    public void SyntaxCollections_RetainCompletedConstructorInputs()
    {
        var wordParts = new List<WordPart> { WordPart.FromText(WordPartKind.Unquoted, "value") };
        var word = new Word(wordParts);
        var command = new SimpleCommand([], [word], [], SimpleCommand.NoDeclarationAssignments);
        var commands = new List<ICommand> { command };
        var script = new ParsedScript(commands);
        var loopItems = new List<Word> { word };
        var loop = new ForCommand("item", loopItems, command);
        var patterns = new List<Word> { word };
        var caseItem = new CaseItem(patterns, command);
        var caseItems = new List<CaseItem> { caseItem };
        var caseCommand = new CaseCommand(word, caseItems);
        var elifClauses = new List<ElifClause> { new(command, command) };
        var ifCommand = new IfCommand(command, command, elifClauses, null);
        var redirections = new List<Redirection>();
        var redirected = new RedirectedCommand(command, redirections);

        // Construction retains completed lists; callers must not mutate them afterward.
        Assert.Same(wordParts, word.Parts);
        Assert.Same(commands, script.Commands);
        Assert.Same(loopItems, loop.Items);
        Assert.Same(patterns, caseItem.Patterns);
        Assert.Same(caseItems, caseCommand.Items);
        Assert.Same(elifClauses, ifCommand.ElifClauses);
        Assert.Same(redirections, redirected.Redirections);
    }

    [Fact]
    public void ExpandedWord_RetainsCompletedSegments()
    {
        var segments = new List<ExpandedSegment>
        {
            new(ShellValue.FromText("first"), isGlobQuoted: false)
        };
        var word = new ExpandedWord(segments);

        Assert.Same(segments, word.Segments);
        Assert.Equal("first", word.ToText(new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes))));
        Assert.False(word.Segments[0].IsGlobQuoted);
    }
}
