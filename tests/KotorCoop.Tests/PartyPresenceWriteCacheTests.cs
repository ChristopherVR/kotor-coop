using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PartyPresenceWriteCache"/> — round 103 (docs/research/party-reconciliation.md),
/// the fix for "reconnect does not reliably re-establish companion mirroring" round 102's rig
/// pass found. The root cause: <c>SessionManager</c> keeps ONE <c>KotorGameBridge</c> (and
/// therefore one of these caches) alive across a disconnect and the reconnect that follows it,
/// so a cache that only ever cleared on a module change survived a reconnect untouched — a
/// reconnect whose first party snapshot read byte-identical to whatever was mirrored right
/// before the disconnect found the cache still agreeing with it and skipped rewriting the
/// PartyPresenceCount level <c>ClearCoopState</c> had just zeroed. These tests pin the exact
/// shape of that bug and confirm <see cref="PartyPresenceWriteCache.Reset"/> closes it.
/// </summary>
public sealed class PartyPresenceWriteCacheTests
{
    private static PartyMemberState Member(byte slot = 0, float x = 1f) =>
        new(slot, new Vector3f(x, 2f, 3f), 0.5f, 7u, 34);

    [Fact]
    public void FirstSlotWriteIsNeverSkipped()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);

        Assert.False(cache.CanSkipSlot(0, Member(), moduleChanged: false));
    }

    [Fact]
    public void IdenticalContentIsSkippedOnceRecorded()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);
        PartyMemberState m = Member();

        cache.RecordSlot(0, m);

        Assert.True(cache.CanSkipSlot(0, m, moduleChanged: false));
    }

    [Fact]
    public void CountWriteIsSkippedOnlyOnceRecordedIdentical()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);

        Assert.False(cache.CanSkipCount(1, moduleChanged: false));

        cache.RecordCount(1);

        Assert.True(cache.CanSkipCount(1, moduleChanged: false));
        Assert.False(cache.CanSkipCount(2, moduleChanged: false));
    }

    [Fact]
    public void ModuleChangeForcesAWriteEvenWithIdenticalContent()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);
        PartyMemberState m = Member();
        cache.RecordSlot(0, m);
        cache.RecordCount(1);

        Assert.False(cache.CanSkipSlot(0, m, moduleChanged: true));
        Assert.False(cache.CanSkipCount(1, moduleChanged: true));
    }

    [Fact]
    public void NoteModuleReportsChangedOnlyOnAnActualDifference()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);

        // First observation of a real module name is always a change (starts blank).
        Assert.True(cache.NoteModule("end_m01aa"));

        // Same module again: no change.
        Assert.False(cache.NoteModule("end_m01aa"));

        // A different module: a change again.
        Assert.True(cache.NoteModule("end_m01ab"));
    }

    /// <summary>⚠ THE EXACT ROUND-102 SHAPE. Simulates one full disconnect/reconnect cycle
    /// against the SAME cache instance — matching SessionManager's own "one bridge across every
    /// reconnect" contract — where the host's companion has not moved (the ordinary case: the
    /// player who disconnected is standing still, or nobody is watching). Without
    /// <see cref="PartyPresenceWriteCache.Reset"/>, both the slot and the count write are
    /// wrongly skipped on the very first post-reconnect pass.</summary>
    [Fact]
    public void WithoutResetAByteIdenticalReconnectSkipsTheVeryWriteThatMustLand()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);
        cache.NoteModule("end_m01aa");
        PartyMemberState m = Member();

        // Pre-disconnect: an ordinary mirrored pass.
        Assert.False(cache.CanSkipSlot(0, m, moduleChanged: false));
        cache.RecordSlot(0, m);
        Assert.False(cache.CanSkipCount(1, moduleChanged: false));
        cache.RecordCount(1);

        // Disconnect happens here. ClearCoopState zeroes the BLOCK's own PartyPresenceCount
        // directly (a raw memory write this cache never sees) — but WITHOUT calling Reset(),
        // this cache's own idea of "what I last wrote" survives untouched.

        // Reconnect: the host republishes the exact same companion state (nothing moved).
        // Same module, same content -> the bug: both skips fire, so the block's own count
        // (zeroed by ClearCoopState) is never rewritten from 0.
        Assert.True(cache.CanSkipSlot(0, m, moduleChanged: false));
        Assert.True(cache.CanSkipCount(1, moduleChanged: false));
    }

    /// <summary>The fix: <see cref="PartyPresenceWriteCache.Reset"/>, called from
    /// <c>KotorGameBridge.ClearCoopState()</c> on every disconnect, makes the same reconnect
    /// scenario above write unconditionally on its first pass, even though the content is
    /// byte-identical to what was mirrored before the disconnect.</summary>
    [Fact]
    public void ResetMakesTheFirstPostReconnectWriteLandEvenWithIdenticalContent()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);
        cache.NoteModule("end_m01aa");
        PartyMemberState m = Member();

        cache.RecordSlot(0, m);
        cache.RecordCount(1);

        // The fix: ClearCoopState's disconnect path now calls this.
        cache.Reset();

        // Reconnect, same module: NoteModule itself reports "changed" the FIRST time it is
        // called after a reset (the blanked baseline never matches a real name) — already
        // enough on its own to force the next write through the module-changed override — so
        // call it a SECOND time here to reach a genuinely false moduleChanged before proving
        // the slot/count caches THEMSELVES (not just that override) were actually cleared.
        Assert.True(cache.NoteModule("end_m01aa"));
        Assert.False(cache.NoteModule("end_m01aa"));

        Assert.False(cache.CanSkipSlot(0, m, moduleChanged: false));
        Assert.False(cache.CanSkipCount(1, moduleChanged: false));
    }

    [Fact]
    public void ResetAlsoForgetsTheModuleBaseline()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);
        cache.NoteModule("end_m01aa");

        cache.Reset();

        // The cached module is blanked, so re-observing the SAME module name after a reset is
        // itself reported as a change (an empty cached module never matches a real one) — the
        // same "always force a fresh write across a boundary" behaviour a genuine module
        // transition gets.
        Assert.True(cache.NoteModule("end_m01aa"));
    }

    [Fact]
    public void DifferentContentIsNeverSkippedRegardlessOfCache()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);
        PartyMemberState before = Member(x: 1f);
        PartyMemberState after = Member(x: 5f);
        cache.RecordSlot(0, before);

        Assert.False(cache.CanSkipSlot(0, after, moduleChanged: false));
    }

    [Fact]
    public void SlotsAreIndependent()
    {
        PartyPresenceWriteCache cache = new(capacity: 4);
        PartyMemberState m = Member();

        cache.RecordSlot(0, m);

        // Slot 1 has never been recorded, so it is not skipped even though slot 0's content
        // matches — capacity must not bleed one slot's cache into another's.
        Assert.False(cache.CanSkipSlot(1, m, moduleChanged: false));
    }
}
