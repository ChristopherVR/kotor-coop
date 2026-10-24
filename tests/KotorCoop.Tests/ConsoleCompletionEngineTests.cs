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

    // ---- CompleteReverse (Shift+Tab) --------------------------------------------

    [Fact]
    public void ReverseThenForwardReturnsToTheSameCandidate()
    {
        ConsoleCompletionEngine engine = NewEngine();

        string forward = engine.Complete("c") ?? throw new InvalidOperationException("expected a completion");
        string forwardAgain = engine.Complete(forward) ?? throw new InvalidOperationException("expected a completion");
        Assert.NotEqual(forward, forwardAgain);

        string? back = engine.CompleteReverse(forwardAgain);
        Assert.Equal(forward, back);
    }

    [Fact]
    public void ReverseOnAFreshPrefixStartsFromTheLastCandidate()
    {
        // c -> connect, clear, cls (declared order); reverse should reach "cls" first.
        Assert.Equal("cls", NewEngine().CompleteReverse("c"));
    }

    [Fact]
    public void ReverseWrapsBackToTheLastCandidate()
    {
        ConsoleCompletionEngine engine = NewEngine();
        Assert.Equal("cls", engine.CompleteReverse("c"));
        Assert.Equal("clear", engine.CompleteReverse("cls"));
        Assert.Equal("connect", engine.CompleteReverse("clear"));

        // Wraps back to the end of the list.
        Assert.Equal("cls", engine.CompleteReverse("connect"));
    }

    [Fact]
    public void ReverseDoesNothingPastCompletion()
    {
        ConsoleCompletionEngine engine = NewEngine();
        Assert.Null(engine.CompleteReverse("connect "));
    }

    // ---- CandidatesFor -----------------------------------------------------------

    [Fact]
    public void CandidatesForListsEveryMatch()
    {
        Assert.Equal(["connect", "clear", "cls"], NewEngine().CandidatesFor("c"));
    }

    [Fact]
    public void CandidatesForIsEmptyOnceAnArgumentFollows()
    {
        Assert.Empty(NewEngine().CandidatesFor("connect "));
    }

    [Fact]
    public void CandidatesForIsEmptyOnAnEmptyBuffer()
    {
        Assert.Empty(NewEngine().CandidatesFor(""));
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
    public void VerbWithArgumentsHintsWhichArgumentIsBeingTyped()
    {
        // A trailing space means the previous token finished, so the marker moves on; while
        // a token is still being typed it stays on that token.
        Assert.Equal("connect <ip|#n> [password]  <- <ip|#n>", NewEngine().Hint("connect "));
        Assert.Equal("connect <ip|#n> [password]  <- <ip|#n>", NewEngine().Hint("connect 10.0.0.5"));
        Assert.Equal("connect <ip|#n> [password]  <- [password]", NewEngine().Hint("connect 10.0.0.5 "));
        Assert.Equal("connect <ip|#n> [password]  <- [password]", NewEngine().Hint("connect 10.0.0.5 hoth"));
    }

    [Fact]
    public void EveryArgumentGivenSaysSo()
    {
        Assert.Equal("connect <ip|#n> [password]  <- (all given)", NewEngine().Hint("connect 10.0.0.5 hoth "));
    }

    [Fact]
    public void AVerbWithNoArgumentsJustShowsItsUsage()
    {
        Assert.Equal("clear", NewEngine().Hint("clear "));
    }

    [Fact]
    public void AParentheticalNoteIsNotAnArgument()
    {
        // The catalog carries prose after the arguments for a couple of verbs; it must not
        // be offered as the next thing to type.
        ConsoleCompletionEngine engine = new([("slay", "slay [tag|id]  (blank = nearest hostile)")]);
        Assert.Equal("slay [tag|id]  (blank = nearest hostile)  <- [tag|id]", engine.Hint("slay "));
        Assert.Equal("slay [tag|id]  (blank = nearest hostile)  <- (all given)", engine.Hint("slay foo "));
    }

    // ---- the engine's own verbs -------------------------------------------------

    [Fact]
    public void EngineVerbsCompleteAndCoopVerbsComeFirst()
    {
        ConsoleCompletionEngine engine = NewEngine();
        engine.SetEngineVerbs(["giveitem", "givecredits", "clear"]);

        // Ours lead: "c" reaches connect/clear/cls before anything the engine registered.
        Assert.Equal("connect", engine.Complete("c"));

        // And an engine-only verb is reachable, which it was not before.
        Assert.Equal("givecredits", engine.Complete("give"));
        Assert.Equal("giveitem", engine.Complete("givecredits"));
    }

    [Fact]
    public void AnEngineVerbAlreadyInTheCatalogIsNotOfferedTwice()
    {
        ConsoleCompletionEngine engine = NewEngine();
        engine.SetEngineVerbs(["clear", "clear"]);

        Assert.Equal("connect  clear  cls", engine.Hint("c"));
    }

    [Fact]
    public void AnEngineVerbHintsThatTheGameOwnsIt()
    {
        ConsoleCompletionEngine engine = NewEngine();
        engine.SetEngineVerbs(["revealmap"]);

        Assert.Equal("revealmap  (game console command)", engine.Hint("revealmap"));
        Assert.Equal("revealmap  (game console command)", engine.Hint("revealmap now"));
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

    [Fact]
    public void EveryCompletedVerbIsRoutedToTheAgent()
    {
        // The other half of the sync demand: a verb that completes and parses but is missing
        // from the detour's list never reaches the agent; the retail FuncInterp answers it with
        // "function not found" (steam_status and pooladd both shipped that way).
        HashSet<string> routed = [.. ConsoleRoutedVerbs.All];
        string[] missing = [.. ConsoleVerbCatalog.Entries.Select(e => e.Verb).Where(v => !routed.Contains(v))];

        Assert.True(
            missing.Length == 0,
            $"these catalog verbs are not in ConsoleRoutedVerbs, so the console never routes them: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The verbs KOTOR itself registers in the developer console, read out of the retail
    /// binary's string table (the block at file offset 0x34F058..0x34F210 in the GOG 1.0.3
    /// swkotor.exe; see docs/research/console-cheats.md).
    /// </summary>
    private static readonly string[] RetailConsoleVerbs =
    [
        "heal", "setcomputeruse", "setdemolitions", "setstealth", "setawareness",
        "setpersuade", "setrepair", "setsecurity", "settreatinjury", "setstrength",
        "setdexterity", "setconstitution", "setintelligence", "setwisdom", "setcharisma",
        "addexp", "turbo", "invulnerability", "restartminigame", "bright", "addlightside",
        "adddarkside", "revealmap", "givecredits", "giveitem", "givemed", "giverepair",
        "givecomspikes", "givesecspikes", "givesitharmour", "warp", "infiniteuses",
        "whereami", "addlevel", "dancedancemalak",
    ];

    /// <summary>
    /// A co-op verb must not SHADOW a retail one. The console interceptor swallows any line
    /// whose first token is ours, so naming a co-op verb `heal` would take the game's own
    /// working cheat away from the player and replace it with something they did not ask
    /// for — and they would have no way to tell, because the engine never sees the line.
    ///
    /// <c>warp</c> is the one deliberate exception and predates the staging cheats: co-op
    /// travel has to go through the session, not the raw cheat.
    /// </summary>
    [Fact]
    public void NoCoopVerbShadowsARetailConsoleVerb()
    {
        HashSet<string> retail = [.. RetailConsoleVerbs];
        List<string> shadowed = [];

        foreach (string verb in ConsoleVerbCatalog.Entries.Select(e => e.Verb).Concat(ConsoleRoutedVerbs.All).Distinct())
        {
            if (retail.Contains(verb) && verb != "warp")
            {
                shadowed.Add(verb);
            }
        }

        Assert.True(
            shadowed.Count == 0,
            $"these co-op verbs would swallow a retail console cheat: {string.Join(", ", shadowed)}");
    }
}
