using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class ConsoleCompletionEngineTests
{
    /// <summary>A small catalog with known prefix overlaps, so tests read clearly.</summary>
    private static readonly (string Verb, string Usage)[] Catalog =
    [
        ("connect", "connect <ip|#n> [password]"),
        ("clear", "clear"),
        ("cls", "cls"),
        ("host", "host [password]"),
        ("stat", "stat"),
        ("status", "status"),
    ];

    private static ConsoleCompletionEngine NewEngine() => new(Catalog);

    // ---- Complete --------------------------------------------------------------

    [Fact]
    public void CompletesAUniquePrefix()
    {
        Assert.Equal("host", NewEngine().Complete("ho"));
    }

    [Fact]
    public void RepeatedTabCyclesThroughCandidatesAndWraps()
    {
        ConsoleCompletionEngine engine = NewEngine();

        string? first = engine.Complete("c");
        Assert.Equal("connect", first);

        // The controller writes the completion into the buffer, so the next call sees it.
        Assert.Equal("clear", engine.Complete("connect"));
        Assert.Equal("cls", engine.Complete("clear"));

        // Wrap back to the first candidate of the ORIGINAL prefix.
        Assert.Equal("connect", engine.Complete("cls"));
    }

    [Fact]
    public void TypingRestartsTheCycleFromTheNewPrefix()
    {
        ConsoleCompletionEngine engine = NewEngine();
        Assert.Equal("connect", engine.Complete("c"));

        // The player typed an 'l' after our completion cycle started: new prefix, new cycle.
        Assert.Equal("clear", engine.Complete("cl"));
        Assert.Equal("cls", engine.Complete("clear"));
    }

    [Fact]
    public void NoMatchesCompletesNothing()
    {
        Assert.Null(NewEngine().Complete("giveitem"));
    }

    [Fact]
    public void ArgumentsArePastCompletion()
    {
        ConsoleCompletionEngine engine = NewEngine();
        Assert.Null(engine.Complete("connect 10.0."));
        Assert.Null(engine.Complete("connect ")); // even a trailing space finishes the verb
    }

    [Fact]
    public void EmptyBufferCyclesThroughEveryVerb()
    {
        ConsoleCompletionEngine engine = NewEngine();
        Assert.Equal("connect", engine.Complete(""));
        Assert.Equal("clear", engine.Complete("connect"));
    }

    [Fact]
    public void PrefixMatchingIsCaseInsensitive()
    {
        Assert.Equal("host", NewEngine().Complete("HO"));
    }

    [Fact]
    public void LeadingSlashAndWhitespaceArePreserved()
    {
        Assert.Equal("/connect", NewEngine().Complete("/co"));
        Assert.Equal("  host", NewEngine().Complete("  ho"));
    }

    [Fact]
    public void AnExactVerbThatPrefixesAnotherStillCycles()
    {
        ConsoleCompletionEngine engine = NewEngine();
        Assert.Equal("stat", engine.Complete("stat"));
        Assert.Equal("status", engine.Complete("stat"));
    }

    [Fact]
    public void ResetForgetsTheCycle()
    {
        ConsoleCompletionEngine engine = NewEngine();
        Assert.Equal("connect", engine.Complete("c"));
        engine.Reset();

        // Without the reset this would cycle onward to "clear"; after it, "connect" is a
        // fresh prefix whose only candidate is itself.
        Assert.Equal("connect", engine.Complete("connect"));
    }

    // ---- Hint ------------------------------------------------------------------

    [Fact]
    public void PartialTokenHintsTheCandidateList()
    {
        Assert.Equal("connect  clear  cls", NewEngine().Hint("c"));
    }

    [Fact]
    public void UniqueVerbHintsItsUsage()
    {
        Assert.Equal("host [password]", NewEngine().Hint("host"));
        Assert.Equal("host [password]", NewEngine().Hint("ho")); // sole candidate: show where it lands
    }

    [Fact]
    public void VerbWithArgumentsHintsItsUsage()
    {
        Assert.Equal("connect <ip|#n> [password]", NewEngine().Hint("connect 10.0.0.5"));
        Assert.Equal("connect <ip|#n> [password]", NewEngine().Hint("connect "));
    }

    [Fact]
    public void ExactVerbThatPrefixesAnotherHintsTheList()
    {
        // "stat" is complete AND extends to "status" — the list shows both options.
        Assert.Equal("stat  status", NewEngine().Hint("stat"));
    }

    [Fact]
    public void UnknownTokenHintsNothing()
    {
        Assert.Equal(string.Empty, NewEngine().Hint("giveitem"));
        Assert.Equal(string.Empty, NewEngine().Hint("giveitem g_w_lghtsbr01"));
    }

    [Fact]
    public void EmptyBufferHintsNothing()
    {
        Assert.Equal(string.Empty, NewEngine().Hint(""));
        Assert.Equal(string.Empty, NewEngine().Hint("   "));
    }

    [Fact]
    public void OverlongCandidateListsAreCappedToOneLine()
    {
        (string, string)[] wide = new (string, string)[30];
        for (int i = 0; i < wide.Length; i++)
        {
            wide[i] = ($"verb{i:D2}xxxx", "usage");
        }

        ConsoleCompletionEngine engine = new(wide);
        string hint = engine.Hint("v");
        Assert.True(hint.Length <= 80, $"hint too long ({hint.Length}): {hint}");
        Assert.EndsWith("...", hint);
    }

    // ---- catalog <-> parser sync ----------------------------------------------

    [Fact]
    public void EveryCatalogVerbParsesToACoopCommand()
    {
        // The half of the sync demand a unit test can enforce: nothing the completion
        // engine offers may land in the parser as Unknown (an argument-less parse may be
        // Invalid — "connect" alone is — but Invalid is still ours and still replies).
        foreach ((string verb, _) in ConsoleVerbCatalog.Entries)
        {
            CoopCommandKind kind = ConsoleCommand.Parse(verb).Kind;
            Assert.True(
                kind != CoopCommandKind.Unknown && kind != CoopCommandKind.None,
                $"catalog verb '{verb}' is not owned by ConsoleCommand.Parse (got {kind})");
        }
    }

    [Fact]
    public void EveryCatalogUsageStartsWithItsVerb()
    {
        foreach ((string verb, string usage) in ConsoleVerbCatalog.Entries)
        {
            Assert.StartsWith(verb, usage);
        }
    }
}
