using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="JoinModeRule"/> — how a joiner gets a character into the host's world.
///
/// The properties worth pinning are the ones whose failure is silent rather than loud: a
/// misspelled <c>join_mode</c> that lands in a mode the player did not ask for; an explicit
/// <c>newcharacter</c> quietly overruled by a character that happens to be on disk; the
/// default mode mistaking a single-player save for the co-op character; and the first-time
/// joiner with no save at all, who used to be handed to the ordinary load-slot-0 path and told
/// their SAVEGAME.sav was missing.
/// </summary>
public class JoinModeRuleTests
{
    [Theory]
    [InlineData("newcharacter")]
    [InlineData("NewCharacter")]
    [InlineData("  newcharacter  ")]
    [InlineData("new_character")]
    [InlineData("new-character")]
    [InlineData("new character")]
    public void EveryWayPeopleWriteNewCharacterParses(string written) =>
        Assert.Equal(JoinMode.NewCharacter, JoinModeRule.Parse(written));

    [Theory]
    [InlineData("save")]
    [InlineData("SAVE")]
    [InlineData("  save ")]
    public void SaveIsTheExplicitOptOut(string written) =>
        Assert.Equal(JoinMode.Save, JoinModeRule.Parse(written));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("nwecharacter")]   // a typo must never become a forced reroll
    [InlineData("newcharacters")]
    [InlineData("saves")]          // nor the legacy own-save path
    [InlineData("yes")]
    public void AnythingElseIsTheDefault(string? written) =>
        Assert.Equal(JoinMode.Auto, JoinModeRule.Parse(written));

    [Theory]
    [InlineData(JoinMode.Auto)]
    [InlineData(JoinMode.Save)]
    [InlineData(JoinMode.NewCharacter)]
    public void FormatRoundTrips(JoinMode mode) =>
        Assert.Equal(mode, JoinModeRule.Parse(JoinModeRule.Format(mode)));

    // ---- join_mode = auto (the default) ----

    /// <summary>The whole point of the default: a single-player save on the install is NOT a
    /// character to join with. The first join makes one.</summary>
    [Fact]
    public void AutoWithNoCoopCharacterCreatesOneEvenWhenASaveExists()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Auto,
            hasCoopCharacter: false,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: true,
            autoAdoptEnabled: true,
            out string reason);

        Assert.Equal(JoinAction.CreateCharacterThenAdopt, action);
        Assert.Contains("no co-op character", reason);
    }

    /// <summary>...and once one exists it is used, full stop — a newer single-player save does
    /// not outrank it, and a matching world does not shortcut around it.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AutoWithACoopCharacterAlwaysCarriesIt(bool hasOwnCharacter, bool saveMatches)
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Auto,
            hasCoopCharacter: true,
            hasOwnCharacter,
            saveIdentityMatchesHost: saveMatches,
            autoAdoptEnabled: true,
            out string reason);

        Assert.Equal(JoinAction.AdoptWithCoopCharacter, action);
        Assert.Contains("co-op character", reason);
    }

    /// <summary>Adoption is the only mechanism that carries a character into another install's
    /// world; with it switched off the default mode degrades to the pre-adoption behaviour the
    /// player asked for, not to a refusal.</summary>
    [Fact]
    public void AutoWithAdoptionOffFallsBackToTheOwnSavePath()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Auto,
            hasCoopCharacter: true,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: false,
            autoAdoptEnabled: false,
            out string reason);

        Assert.Equal(JoinAction.LoadOwnSave, action);
        Assert.Contains("auto_adopt_world is off", reason);
    }

    // ---- join_mode = newcharacter ----

    [Fact]
    public void NewCharacterWinsOverAnExistingSave()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.NewCharacter,
            hasCoopCharacter: false,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: true,
            autoAdoptEnabled: true,
            out string reason);

        Assert.Equal(JoinAction.CreateCharacterThenAdopt, action);
        Assert.Contains("newcharacter", reason);
    }

    /// <summary>The reroll: an existing co-op character is exactly what the player asked to
    /// replace.</summary>
    [Fact]
    public void NewCharacterWinsOverAnExistingCoopCharacter()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.NewCharacter,
            hasCoopCharacter: true,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: false,
            autoAdoptEnabled: true,
            out _);

        Assert.Equal(JoinAction.CreateCharacterThenAdopt, action);
    }

    // ---- join_mode = save (the legacy path, unchanged) ----

    [Fact]
    public void AMatchingSaveJustLoads()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Save,
            hasCoopCharacter: false,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: true,
            autoAdoptEnabled: true,
            out _);

        Assert.Equal(JoinAction.LoadOwnSave, action);
    }

    [Fact]
    public void ADifferentWorldAdoptsCarryingThisSavesCharacter()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Save,
            hasCoopCharacter: false,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: false,
            autoAdoptEnabled: true,
            out _);

        Assert.Equal(JoinAction.AdoptWithOwnCharacter, action);
    }

    /// <summary>join_mode = save ignores the sidecar at decision time — the rejoin path's own
    /// "newer and same host" preference (<c>ClientCharacterPersistence.ShouldPreferSidecar</c>)
    /// is where a save-mode joiner's session-saved character gets considered.</summary>
    [Fact]
    public void SaveModeDoesNotAdoptTheCoopCharacterDirectly()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Save,
            hasCoopCharacter: true,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: false,
            autoAdoptEnabled: true,
            out _);

        Assert.Equal(JoinAction.AdoptWithOwnCharacter, action);
    }

    /// <summary>The regression this whole feature exists for: no save, no character, and
    /// (before this) no way in at all.</summary>
    [Fact]
    public void AFirstTimeJoinerWithNoSaveIsOfferedCharacterCreationEvenInSaveMode()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Save,
            hasCoopCharacter: false,
            hasOwnCharacter: false,
            saveIdentityMatchesHost: false,
            autoAdoptEnabled: true,
            out string reason);

        Assert.Equal(JoinAction.CreateCharacterThenAdopt, action);
        Assert.Contains("no character on this install", reason);
    }

    [Fact]
    public void AutoAdoptOffKeepsThePreAdoptionBehaviourExactly()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Save,
            hasCoopCharacter: false,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: false,
            autoAdoptEnabled: false,
            out string reason);

        Assert.Equal(JoinAction.LoadOwnSave, action);
        Assert.Contains("auto_adopt_world is off", reason);
    }

    /// <summary>Nothing to load and nothing allowed to be fetched. Refusing at the menu with a
    /// reason beats arriving in a world as a blank level-1 stranger, which is what the engine
    /// does when handed no character (world-adoption.md §7).</summary>
    [Fact]
    public void NoCharacterAndNoAdoptionIsARefusalRatherThanAnAttempt()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Save,
            hasCoopCharacter: false,
            hasOwnCharacter: false,
            saveIdentityMatchesHost: false,
            autoAdoptEnabled: false,
            out _);

        Assert.Equal(JoinAction.Refuse, action);
    }

    /// <summary>auto_adopt_world must not be consulted at all when the worlds already agree —
    /// otherwise turning it off would break a perfectly ordinary identical-save join.</summary>
    [Fact]
    public void AutoAdoptOffStillLoadsAMatchingSave()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Save,
            hasCoopCharacter: false,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: true,
            autoAdoptEnabled: false,
            out _);

        Assert.Equal(JoinAction.LoadOwnSave, action);
    }
}
