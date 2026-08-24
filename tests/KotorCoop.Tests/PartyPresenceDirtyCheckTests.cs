using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PartyPresenceDirtyCheck"/> — the pure half of
/// <c>KotorGameBridge.TryWritePartyMembers</c>'s per-slot dirty check
/// (docs/research/lag-audit.md, the 2026-08-24 <c>party_fast_pump</c> follow-up entry): skip a
/// slot's engine write when the block already holds what would be written again, but never on a
/// module change, since <c>PluginHost.Invalidate()</c> can clear the block independent of what
/// this agent last wrote.
/// </summary>
public sealed class PartyPresenceDirtyCheckTests
{
    private static PartyMemberState Member(byte slot = 0, float x = 1f) =>
        new(slot, new Vector3f(x, 2f, 3f), 0.5f, 7u, 34);

    [Fact]
    public void FirstWriteIsNeverSkipped()
    {
        // lastWritten is null before anything has ever landed in the block.
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(Member(), null, moduleChanged: false));
    }

    [Fact]
    public void IdenticalCandidateIsSkipped()
    {
        PartyMemberState m = Member();
        Assert.True(PartyPresenceDirtyCheck.CanSkipWrite(m, m, moduleChanged: false));
    }

    [Fact]
    public void BothEmptyIsSkipped()
    {
        Assert.True(PartyPresenceDirtyCheck.CanSkipWrite(null, null, moduleChanged: false));
    }

    [Fact]
    public void ChangedPositionIsNotSkipped()
    {
        PartyMemberState before = Member(x: 1f);
        PartyMemberState after = Member(x: 1.01f);
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(after, before, moduleChanged: false));
    }

    [Fact]
    public void SlotBecomingEmptyIsNotSkipped()
    {
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(null, Member(), moduleChanged: false));
    }

    [Fact]
    public void SlotBecomingOccupiedIsNotSkipped()
    {
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(Member(), null, moduleChanged: false));
    }

    /// <summary>⚠ THE HAZARD THIS EXISTS TO PREVENT. Even byte-identical content must still
    /// write across a module change — PluginHost's own Invalidate() clears the shared block's
    /// party-presence slots in-process on a load, independent of this agent's own cache, so
    /// trusting the content comparison alone would leave the block silently empty after a load
    /// whenever a companion's reported position happened not to change across the transition
    /// (the common case, not a rare one).</summary>
    [Fact]
    public void IdenticalCandidateStillWritesOnModuleChange()
    {
        PartyMemberState m = Member();
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(m, m, moduleChanged: true));
    }

    [Fact]
    public void BothEmptyStillWritesOnModuleChange()
    {
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(null, null, moduleChanged: true));
    }

    [Fact]
    public void DifferentSlotIndexIsNotSkipped()
    {
        // Same coordinates, different roster slot: a real change, not noise.
        PartyMemberState before = Member(slot: 0);
        PartyMemberState after = Member(slot: 1);
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(after, before, moduleChanged: false));
    }

    [Fact]
    public void DifferentHealthIsNotSkipped()
    {
        PartyMemberState before = new(0, new Vector3f(1, 2, 3), 0.5f, 7u, 34);
        PartyMemberState after = before with { Health = 20 };
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(after, before, moduleChanged: false));
    }

    [Fact]
    public void DifferentOneShotSerialIsNotSkipped()
    {
        // A fresh swing/parry one-shot must always land, even if position/facing/health are
        // otherwise unchanged that same pass.
        PartyMemberState before = new(0, new Vector3f(1, 2, 3), 0.5f, 7u, 34, OneShotSerial: 5);
        PartyMemberState after = before with { OneShotSerial = 6 };
        Assert.False(PartyPresenceDirtyCheck.CanSkipWrite(after, before, moduleChanged: false));
    }
}
