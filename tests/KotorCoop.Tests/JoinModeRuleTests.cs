using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="JoinModeRule"/> — how a joiner gets a character into the host's world.
///
/// The properties worth pinning are the ones whose failure is silent rather than loud: a
/// misspelled <c>join_mode</c> that sends someone through character creation they did not ask
/// for; an explicit <c>newcharacter</c> quietly overruled by a save that happens to be on disk;
/// and the first-time joiner with no save at all, who used to be handed to the ordinary
/// load-slot-0 path and told their SAVEGAME.sav was missing.
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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("save")]
    [InlineData("SAVE")]
    [InlineData("nwecharacter")]   // a typo must never become a character creation
    [InlineData("newcharacters")]
    [InlineData("yes")]
    public void AnythingElseIsTheShippedDefault(string? written) =>
        Assert.Equal(JoinMode.Save, JoinModeRule.Parse(written));

    [Fact]
    public void FormatRoundTrips()
    {
        Assert.Equal(JoinMode.Save, JoinModeRule.Parse(JoinModeRule.Format(JoinMode.Save)));
        Assert.Equal(
            JoinMode.NewCharacter, JoinModeRule.Parse(JoinModeRule.Format(JoinMode.NewCharacter)));
    }

    [Fact]
    public void NewCharacterWinsOverAnExistingSave()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.NewCharacter,
            hasOwnCharacter: true,
            saveIdentityMatchesHost: true,
            autoAdoptEnabled: true,
            out string reason);

        Assert.Equal(JoinAction.CreateCharacterThenAdopt, action);
        Assert.Contains("newcharacter", reason);
    }

    [Fact]
    public void AMatchingSaveJustLoads()
    {
        JoinAction action = JoinModeRule.Decide(
            JoinMode.Save,
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
            hasOwnCharacter: true,
            saveIdentityMatchesHost: true,
            autoAdoptEnabled: false,
            out _);

        Assert.Equal(JoinAction.LoadOwnSave, action);
    }
}
