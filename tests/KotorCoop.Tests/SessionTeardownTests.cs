using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What a disconnect must lower in the plugin's shared block.
///
/// This exists because of a reported bug with a very specific shape: after a client
/// disconnected, Trask was back in the party and clicking him did nothing. The cause was one
/// agent-written LEVEL that teardown did not clear — the control-swap suppression at 0x1030,
/// which arms a byte patch in the plugin that turns CClientExoAppInternal::ChangeCharacter
/// (Tab, every party portrait, and the engine's death auto-swap) into an immediate return. It
/// stayed raised for the life of the process. Three more fields were leaking beside it.
///
/// A level that is written while connected and not named in the teardown list keeps its last
/// session value forever, because once the agent is gone nothing else ever lowers it. So the
/// list is the contract, and these are the entries that were paid for.
/// </summary>
public class SessionTeardownTests
{
    private static int Offset(string name)
    {
        foreach ((string entry, int offset) in KotorGameBridge.SessionScopedBlockLevels)
        {
            if (entry == name)
            {
                return offset;
            }
        }

        Assert.Fail($"'{name}' is not in the session teardown list");
        return -1;
    }

    /// <summary>
    /// The two death suppressions. Both are byte patches that stay armed while the word reads
    /// non-zero, and a client raises the swap one at its first death pump and never lowers it.
    /// The offsets are the plugin's own SharedBlock.OffsetSuppressGameOver / OffsetSuppressSwap.
    /// </summary>
    [Fact]
    public void TheDeathSuppressionsAreLowered()
    {
        Assert.Equal(0x102C, Offset("game-over suppression"));
        Assert.Equal(0x1030, Offset("control-swap suppression"));
    }

    /// <summary>
    /// The peer interaction locks. The plugin's pass has no command gate (a lock has to
    /// survive the frames around a transition), so a leftover count means it keeps clearing
    /// the named creature's action queue several times a second — an NPC that ignores every
    /// order the player gives it.
    /// </summary>
    [Fact]
    public void ThePeerInteractionLocksAreLowered() =>
        Assert.Equal(0x1358, Offset("peer interaction locks"));

    /// <summary>
    /// The peer's party roster. PartyRosterController's own comment already claimed "a
    /// disconnected agent" would leave nothing to reconcile against — it did not, and a
    /// disconnected client went on putting dismissed companions back.
    /// </summary>
    [Fact]
    public void ThePeerPartyRosterIsLowered() =>
        Assert.Equal(0x3304, Offset("peer party roster"));

    /// <summary>
    /// "This instance mirrors the world" — what the portrait hiding and the companions' Neutral
    /// faction hold both key on.
    /// </summary>
    [Fact]
    public void TheMirroringFlagIsLowered() =>
        Assert.Equal(0x2118, Offset("mirroring"));

    /// <summary>
    /// The effective-behaviour-override mask/values (docs/research/mod-compatibility.md's
    /// "Behaviour-flag negotiation" gap): a leftover override would keep telling the plugin
    /// the disconnected session's host still wants effect_sync/trigger_relay/liveness_sync/
    /// map_reveal_sync/dialogue_recruit_sync overridden — exactly the class of bug this list
    /// exists to catch, for a channel that did not exist when the list was first written.
    /// </summary>
    [Fact]
    public void TheBehaviourOverridesAreLowered()
    {
        Assert.Equal(0x40C0, Offset("behaviour override mask"));
        Assert.Equal(0x40C4, Offset("behaviour override values"));
    }

    /// <summary>
    /// The command is the plugin's "a session is driving this instance", and its FALLING edge
    /// is what runs the plugin's own release. Lowering it before the rest would run that
    /// release against a block still full of the previous session's levels.
    /// </summary>
    [Fact]
    public void TheCommandIsLoweredLast()
    {
        (string name, int offset) = KotorGameBridge.SessionScopedBlockLevels[^1];
        Assert.Equal("command", name);
        Assert.Equal(0x254, offset);
    }

    /// <summary>A repeated offset is two names for one field, which means one of them is
    /// wrong — and a wrong offset in this list clears something else and leaves the real
    /// field raised, which is the exact bug all over again.</summary>
    [Fact]
    public void NoFieldIsListedTwice()
    {
        HashSet<int> seen = [];
        HashSet<string> names = [];

        foreach ((string name, int offset) in KotorGameBridge.SessionScopedBlockLevels)
        {
            Assert.True(seen.Add(offset), $"offset 0x{offset:X} appears twice ('{name}')");
            Assert.True(names.Add(name), $"'{name}' appears twice");
            Assert.True(offset >= 0 && offset % 4 == 0, $"'{name}' is not a dword offset");
        }
    }
}
