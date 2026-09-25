using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// docs/research/class-and-power-grants.md §11: Zhar's saber-construction reward creates the new
/// saber straight into the builder's OWN backpack, then leaves the conversation on the Upgrade
/// Bench screen — retail does NOT equip it automatically (live-confirmed 2026-09-25 round 2: the
/// builder equipped it by hand, minutes later). An earlier round of this pump watched ONLY the
/// equip-slot edge and would have waited on that hand-equip forever; this round ALSO watches for
/// the saber simply existing in the builder's own possession (backpack/pool or equipped) the
/// moment it is created, so a receiver does not have to wait on the builder ever equipping at all.
///
/// <b>Detect</b> — either <c>PlayerState.EquippedRightWeapon</c>/<c>EquippedLeftWeapon</c> (an
/// equip-slot edge — still checked, cheap, already replicated every tick) OR a rate-limited
/// <c>IGameBridge.ReadPossessedItems</c> sweep of the local player (backpack/pool-inclusive,
/// docs/research/key-items-and-solo.md §1: for a live PC that IS the same repository
/// <c>party_pool_sync</c> already tracks, so this reuses the existing read rather than adding a
/// new one). The first tick either signal shows one of the three canonical starter sabers,
/// announce it.
///
/// <b>Deliver</b> — no new packet. The announcement is one session-wide, sticky flag
/// (<see cref="FirstLightsaberColorRule.FirstSaberBuiltFlagId"/>) on the existing world-progression
/// flag channel (<c>EventFlagStore</c>/<c>FlagOpPacket</c>) — already reliable, already merge-safe,
/// and already resynced whole to a late joiner via the paged world snapshot. No payload beyond
/// "it happened": every receiver (including the publisher's own next tick) decides ENTIRELY from
/// its own locally-read class which colour, if any, it personally needs.
///
/// <b>Apply</b> — while the flag reads true and this player does not yet have a first saber
/// (equipped OR backpack/pool — the SAME possession check the detect half uses, re-checked live,
/// never a one-shot latch, so a resync/rejoin/late join is naturally idempotent) and this
/// player's own class list names a Jedi class, queue exactly one automatic <c>giveloot</c> — the
/// SAME single-slot arbitrated cheat channel every other automatic give in this codebase already
/// uses (<c>CoopAgent.NoteLocalItemGiven</c> feeds the <c>RecentlySyncedItems</c> ledger BEFORE
/// submitting, so <c>ScriptItemGrantPump</c>'s own sweep does not mistake the give for a script
/// grant and re-broadcast it — see §11.2's "round 2" regression). This lands the saber in the
/// receiver's own backpack, unequipped, EXACTLY where retail leaves the builder's own copy
/// (confirmed 2026-09-25 round 2) — never a raw <c>ActionEquipItem</c>/synchronous-equip call,
/// which (a) needs Weapon Proficiency: Lightsaber (granted at the class's OWN first level, not at
/// level 0 — the same reason retail's own construction never auto-equips either) and (b) is what
/// produced round 2's live-caught duplicate (the create step's own transient repository presence
/// was independently re-detected and re-broadcast through TWO different channels). The receiver
/// equips it themselves, in their own time, exactly like the builder does — the game's own
/// prerequisite check then applies normally, because nothing here bypasses it.
/// </summary>
internal sealed class FirstLightsaberBuildPump(
    IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    private static readonly HashSet<uint> LocalPlayerIdSet = [AgentWellKnownIds.LocalPlayerObjectId];

    /// <summary>Minimum gap between the (comparatively expensive) possessed-items sweep this
    /// pump adds on top of the free <c>PlayerState</c> equip-slot fields — matches
    /// <c>KotorGameBridge.PartyPoolScanIntervalMs</c>'s own 1 Hz precedent for the identical
    /// repository read. The equip-slot check still runs every tick regardless (it costs nothing
    /// extra — those fields are already read for the world-state channel).</summary>
    private const int PossessionCheckIntervalMs = 1000;

    /// <summary>Minimum gap between two automatic give ATTEMPTS for this player's own saber —
    /// generous past one single-slot cheat-channel round trip, so a give already dequeued and
    /// in flight (this pump's own queue only guards against a SECOND entry sitting in it, not
    /// against re-enqueueing right after the first was picked up) is not immediately followed by
    /// a second one before <see cref="PossessionCheckIntervalMs"/>'s own rate-limited read has
    /// had a chance to observe it landed.</summary>
    private const int GiveRetryIntervalMs = 3000;

    private long _nextGiveAttemptMs;

    private long _nextPossessionCheckMs;

    private bool _lastPossessionCheck;

    private bool _hasPublishedLocally;

    private bool _hasFirstSaber;

    private readonly Lock _pendingGate = new();

    private string? _pendingBuildResref;

    public void Pump(in PlayerState local)
    {
        if (!settings.FirstSaberColorSync)
        {
            return;
        }

        bool locallyHasOne = HasFirstSaber(local);

        if (locallyHasOne)
        {
            _hasFirstSaber = true;
        }

        PublishIfJustBuilt(locallyHasOne);

        if (_hasFirstSaber)
        {
            return;
        }

        TryGiveOwnSaberIfEligible();
    }

    /// <summary>True if the local player currently has one of the three canonical starter
    /// sabers, equipped OR sitting anywhere <see cref="IGameBridge.ReadPossessedItems"/> can see
    /// it (backpack/pool included — see this class's own doc for why that matters here).</summary>
    private bool HasFirstSaber(in PlayerState local)
    {
        if (FirstLightsaberColorRule.IsCanonicalFirstSaber(local.EquippedRightWeapon)
            || FirstLightsaberColorRule.IsCanonicalFirstSaber(local.EquippedLeftWeapon))
        {
            return true;
        }

        long now = Environment.TickCount64;
        if (now < _nextPossessionCheckMs)
        {
            return _lastPossessionCheck;
        }

        _nextPossessionCheckMs = now + PossessionCheckIntervalMs;
        _lastPossessionCheck = bridge.ReadPossessedItems(LocalPlayerIdSet)
            .Any(item => FirstLightsaberColorRule.IsCanonicalFirstSaber(item.Resref));
        return _lastPossessionCheck;
    }

    private void PublishIfJustBuilt(bool locallyHasOne)
    {
        if (_hasPublishedLocally || !locallyHasOne)
        {
            return;
        }

        _hasPublishedLocally = true;

        if (session.Flags.TryGet(FirstLightsaberColorRule.FirstSaberBuiltFlagId, out sbyte already) && already != 0)
        {
            return; // already known to the party - nothing new to announce
        }

        log("first lightsaber: this character now has a starter lightsaber - announcing it to the party");
        session.SetFlag(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1);
    }

    private void TryGiveOwnSaberIfEligible()
    {
        if (!session.Flags.TryGet(FirstLightsaberColorRule.FirstSaberBuiltFlagId, out sbyte built) || built == 0)
        {
            return; // nobody in the party has built one yet
        }

        string? resref = FirstLightsaberColorRule.ResrefForClasses(bridge.ReadLocalClassIds());
        if (resref is null)
        {
            return; // not (yet) a Jedi - keep checking every tick, a class grant may still land
        }

        long now = Environment.TickCount64;

        lock (_pendingGate)
        {
            if (_pendingBuildResref is not null || now < _nextGiveAttemptMs)
            {
                return; // one request already queued, or too soon since the last attempt
            }

            _nextGiveAttemptMs = now + GiveRetryIntervalMs;
            log($"first lightsaber: giving this character their own '{resref}' from the party's "
                + "construction");
            _pendingBuildResref = resref;
        }
    }

    /// <summary>
    /// Called by whatever already arbitrates the single-slot staging-cheat channel (the command
    /// dispatcher's pending-cheat-serial check), at most once per ITS tick — the same discipline
    /// <c>LootPump.TryDequeuePendingLoot</c> already follows for its own automatic give.
    /// </summary>
    public bool TryDequeuePendingGive(out string resref)
    {
        lock (_pendingGate)
        {
            if (_pendingBuildResref is null)
            {
                resref = string.Empty;
                return false;
            }

            resref = _pendingBuildResref;
            _pendingBuildResref = null;
            return true;
        }
    }
}
