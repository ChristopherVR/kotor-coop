using KotorCoop.Agent;
using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The cinematic staging instrument: the line a person types, the guard that decides whether
/// the engine is touched, and the two-step sequencing that keeps a refused staging from
/// producing a warp with no movie.
///
/// This verb is worth more test than its size suggests. What it arranges is a pre-rendered
/// movie playing across a module transition, and a movie parks the game's main thread inside
/// `WaitForMovie 0x005F6F20` for its whole length — seconds in which neither of the plugin's
/// frame hooks fires and none of its invariants are maintained (docs/research/cinematics.md).
/// Every assertion below is about making that reachable ON PURPOSE and unreachable by
/// accident.
/// </summary>
public class MovieStagingTests
{
    // ---- the typed line ------------------------------------------------------------

    [Fact]
    public void TheWarpFormCarriesTheModuleTheMoviesAndTheMode()
    {
        CoopCommand command = ConsoleCommand.Parse("moviewarp tar_m02af 01c");

        Assert.Equal(CoopCommandKind.StageMovie, command.Kind);
        Assert.Equal("tar_m02af", command.Address);
        Assert.Equal("01c", command.Password);
        Assert.Equal(StageMovieModes.WarpWithMovies, command.Amount);
    }

    /// <summary>Two-movie transitions are real in the base game (<c>06a</c>+<c>05_1C</c>,
    /// <c>17a</c>+<c>11a</c>), so the list has to survive both spellings a person uses.</summary>
    [Theory]
    [InlineData("moviewarp tar_m02af 01c 01f")]
    [InlineData("moviewarp tar_m02af 01c,01f")]
    public void SeveralMoviesArriveAsOneCommaSeparatedList(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.StageMovie, command.Kind);
        Assert.Equal("01c,01f", command.Password);
    }

    [Fact]
    public void QueueOnlyCarriesNoModule()
    {
        CoopCommand command = ConsoleCommand.Parse("moviewarp queue 01c");

        Assert.Equal(CoopCommandKind.StageMovie, command.Kind);
        Assert.Equal(string.Empty, command.Address);
        Assert.Equal("01c", command.Password);
        Assert.Equal(StageMovieModes.QueueOnly, command.Amount);
    }

    [Theory]
    [InlineData("moviewarp clear", StageMovieModes.Clear)]
    [InlineData("moviewarp off", StageMovieModes.Clear)]
    [InlineData("moviewarp show", StageMovieModes.Show)]
    [InlineData("moviewarp status", StageMovieModes.Show)]
    public void TheDisarmAndReportFormsNeedNoArguments(string line, int mode)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.StageMovie, command.Kind);
        Assert.Equal(mode, command.Amount);
    }

    /// <summary>
    /// A typo in this command must not silently arm nothing — and, worse, must not silently
    /// arm something. The engine's own queue add builds <c>MOVIES:&lt;name&gt;</c> and quietly
    /// refuses a file it cannot open, so a mistyped movie produces a transition with no
    /// cinematic that looks exactly like the defect the verb was typed to measure.
    /// </summary>
    [Theory]
    [InlineData("moviewarp")]                              // no arguments at all
    [InlineData("moviewarp tar_m02af")]                    // a module but no movie: that is `warp`
    [InlineData("moviewarp queue")]                        // queue what?
    [InlineData("moviewarp tar-m02af 01c")]                // not a resref
    [InlineData("moviewarp tar_m02af 01c!")]               // not a movie name
    [InlineData("moviewarp tar_m02af 01c 01c")]            // the engine de-duplicates silently
    [InlineData("moviewarp tar_m02af a b c d e f g")]      // past the engine's own six
    [InlineData("moviewarp tar_m02af aaaaaaaaaaaaaaaaa")]  // 17 chars: past a resref
    public void AMalformedStagingLineIsRefusedWithAReason(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.NotEqual(string.Empty, command.Error);
    }

    /// <summary>
    /// The names ride the cheat channel's 32-byte text field. A list that would be truncated
    /// there is refused HERE, because a truncated name is a name the engine refuses for a
    /// reason nobody watching the log could ever see.
    /// </summary>
    [Fact]
    public void AMovieListTooLongForTheWireIsRefused()
    {
        CoopCommand command = ConsoleCommand.Parse("moviewarp tar_m02af aaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbb");

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.Contains("characters", command.Error);
    }

    /// <summary>
    /// The parser is in KotorCoop.Session and the guard in the win-x86 plugin, which cannot
    /// reference each other, so the limits are written twice. This is the test that keeps the
    /// two copies honest — a parser that accepted seven movies would file a request the plugin
    /// refuses, which is a round trip spent to learn something either side already knew.
    /// </summary>
    [Fact]
    public void TheParserAndThePluginAgreeAboutTheLimits()
    {
        // Six is the engine's own ceiling (StartNewModule takes six movie arguments).
        Assert.Equal(CoopCommandKind.StageMovie, ConsoleCommand.Parse("moviewarp m a b c d e f").Kind);
        Assert.Equal(6, MovieStaging.MaxMovies);

        // 16 is a resref; 17 is not.
        Assert.True(MovieStaging.IsMovieName(new string('a', MovieStaging.MaxNameLength)));
        Assert.False(MovieStaging.IsMovieName(new string('a', MovieStaging.MaxNameLength + 1)));

        // And the modes really are the same numbers on both sides.
        Assert.Equal(MovieStaging.ModeWarpWithMovies, StageMovieModes.WarpWithMovies);
        Assert.Equal(MovieStaging.ModeQueueOnly, StageMovieModes.QueueOnly);
        Assert.Equal(MovieStaging.ModeClear, StageMovieModes.Clear);
        Assert.Equal(MovieStaging.ModeShow, StageMovieModes.Show);
    }

    // ---- the wire form the plugin reads back ---------------------------------------

    [Fact]
    public void ThePluginSplitsTheListTheParserProduced()
    {
        Assert.True(MovieStaging.TrySplit("01c,01f", out string[] names, out string error));

        Assert.Equal(["01c", "01f"], names);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("01c,01c")]
    [InlineData("01c,not a name")]
    [InlineData("a,b,c,d,e,f,g")]
    public void ThePluginRefusesAListItCannotStage(string text)
    {
        Assert.False(MovieStaging.TrySplit(text, out string[] names, out string error));

        Assert.Empty(names);
        Assert.NotEqual(string.Empty, error);
    }

    // ---- the guard -----------------------------------------------------------------

    [Fact]
    public void AClearWorldWithAnEmptyQueueStages()
    {
        Assert.Equal(
            MovieStagingVerdict.Stage,
            MovieStaging.Decide(hasClient: true, transitionPending: false, moviePlaying: false, alreadyQueued: 0, nameCount: 1));
    }

    /// <summary>
    /// Each refusal is a different fact about the engine, and each answer has to name the one
    /// it saw: "nothing happened" and "the movie played and the plugin was unreachable for six
    /// seconds" otherwise leave almost identical evidence.
    /// </summary>
    [Fact]
    public void TheGuardRefusesEachDangerousStateByName()
    {
        // (hasClient, transitionPending, moviePlaying, alreadyQueued, nameCount) -> verdict.
        // Written as a table rather than a [Theory] because the verdict type is internal to
        // the plugin (it compiles in here; it is not part of any public surface).
        (bool Client, bool Pending, bool Playing, int Queued, int Names, MovieStagingVerdict Expected)[] cases =
        [
            (false, false, false, 0, 1, MovieStagingVerdict.RefuseNoWorld),
            (true, false, false, 0, 0, MovieStagingVerdict.RefuseNoNames),
            (true, false, false, 0, 7, MovieStagingVerdict.RefuseTooMany),
            (true, true, false, 0, 1, MovieStagingVerdict.RefuseTransitionPending),
            (true, false, true, 0, 1, MovieStagingVerdict.RefuseMoviePlaying),
            (true, false, false, 1, 1, MovieStagingVerdict.RefuseQueueBusy),
        ];

        foreach ((bool client, bool pending, bool playing, int queued, int names, MovieStagingVerdict expected) in cases)
        {
            MovieStagingVerdict verdict = MovieStaging.Decide(client, pending, playing, queued, names);

            Assert.Equal(expected, verdict);
            Assert.NotEqual(string.Empty, MovieStaging.Explain(verdict, queued));
        }
    }

    /// <summary>
    /// The refusal that protects the GAME rather than us: if the engine has already staged a
    /// cinematic for this transition, adding ours changes what the player watches and makes
    /// the resulting stall un-attributable to either.
    /// </summary>
    [Fact]
    public void AQueueTheGameFilledIsNeverAddedTo()
    {
        MovieStagingVerdict verdict =
            MovieStaging.Decide(hasClient: true, transitionPending: false, moviePlaying: false, alreadyQueued: 1, nameCount: 1);

        Assert.Equal(MovieStagingVerdict.RefuseQueueBusy, verdict);
        Assert.Contains("already queued 1", MovieStaging.Explain(verdict, 1));
    }

    // ---- the arm -------------------------------------------------------------------

    [Fact]
    public void AnArmIsSpentExactlyOnce()
    {
        MovieStagingArm arm = new();
        arm.Arm(["01c"], nowMs: 1000);

        Assert.True(arm.IsArmed);

        string[] taken = Assert.IsType<string[]>(arm.Take(1100));
        Assert.Equal(["01c"], taken);
        Assert.False(arm.IsArmed);
        Assert.Null(arm.Take(1200));
    }

    /// <summary>
    /// An arm nothing came to collect must not sit there waiting to attach a cinematic — and
    /// its multi-second main-thread stall — to some unrelated transition minutes later.
    /// </summary>
    [Fact]
    public void AnArmNobodyCollectedExpiresAndSaysSo()
    {
        MovieStagingArm arm = new();
        arm.Arm(["01c"], nowMs: 0);

        Assert.False(arm.Expire(MovieStagingArm.LifetimeMs, out _));

        Assert.True(arm.Expire(MovieStagingArm.LifetimeMs + 1, out string note));
        Assert.Contains("01c", note);
        Assert.False(arm.IsArmed);

        // And it reports nothing on every later frame, rather than repeating itself.
        Assert.False(arm.Expire(MovieStagingArm.LifetimeMs + 2, out _));
    }

    [Fact]
    public void AStaleArmIsNeverSpentEvenIfNobodyExpiredItFirst()
    {
        MovieStagingArm arm = new();
        arm.Arm(["01c"], nowMs: 0);

        Assert.Null(arm.Take(MovieStagingArm.LifetimeMs + 1));
        Assert.False(arm.IsArmed);
    }

    /// <summary>A name armed against a world that no longer exists is the same class of
    /// mistake as every stale pointer in transition-window-races.md.</summary>
    [Fact]
    public void AWorldChangeDropsTheArmOutLoud()
    {
        MovieStagingArm arm = new();
        arm.Arm(["01c", "01f"], nowMs: 0);

        Assert.True(arm.Forget("the world changed under them", out string note));
        Assert.Contains("01c, 01f", note);
        Assert.False(arm.IsArmed);
        Assert.False(arm.Forget("again", out _));
    }

    [Fact]
    public void ArmingAgainReportsWhatItReplaced()
    {
        MovieStagingArm arm = new();
        arm.Arm(["01c"], nowMs: 0);

        Assert.Equal("01c", arm.Arm(["01f"], nowMs: 10));
        Assert.Equal("01f", arm.Describe());
    }

    // ---- the two-step sequencing ---------------------------------------------------

    [Fact]
    public void TheFollowUpRunsOnceAndOnlyForTheAnswerItWasArmedFor()
    {
        int warped = 0;
        int abandoned = 0;

        CheatFollowUp follow = new();
        follow.Arm(() => warped++, () => abandoned++);

        follow.Take(applied: true)?.Invoke();
        follow.Take(applied: true)?.Invoke();

        Assert.Equal(1, warped);
        Assert.Equal(0, abandoned);
        Assert.False(follow.IsArmed);
    }

    /// <summary>
    /// The assertion the whole verb rests on: a refused staging does NOT warp. A movie-less
    /// transition was already tested on 2026-08-15 and proves nothing about the hypothesis
    /// this instrument exists to settle, so running one by accident would waste a live pass.
    /// </summary>
    [Fact]
    public void ARefusedStagingRunsTheAbandonBranchAndNotTheWarp()
    {
        int warped = 0;
        int abandoned = 0;

        CheatFollowUp follow = new();
        follow.Arm(() => warped++, () => abandoned++);

        follow.Take(applied: false)?.Invoke();

        Assert.Equal(0, warped);
        Assert.Equal(1, abandoned);
    }

    [Fact]
    public void FilingAnotherCheatAbandonsTheFollowUpWithoutRunningIt()
    {
        int ran = 0;

        CheatFollowUp follow = new();
        follow.Arm(() => ran++, () => ran++);
        follow.Clear();

        follow.Take(applied: true)?.Invoke();

        Assert.Equal(0, ran);
    }

    [Fact]
    public void AnOrdinaryCheatArmsNothing()
    {
        CheatFollowUp follow = new();

        Assert.False(follow.IsArmed);
        Assert.Null(follow.Take(applied: true));
        Assert.Null(follow.Take(applied: false));
    }
}
