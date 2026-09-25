using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// docs/research/class-and-power-grants.md §11: Zhar's saber-construction reward equips the new
/// saber into a weapon slot the SAME script tick it creates it (<c>ActionEquipItem</c>, queued
/// via <c>AssignCommand</c>, inside the still-open Zhar conversation) — so it never sits as a
/// backpack row long enough for the generic dialogue/script item-grant diff
/// (<c>DialoguePump</c>/<c>ScriptItemGrantPump</c>) to see it as "a new item" at all.
/// Live-confirmed 2026-09-25: a joiner-led build produced NO <c>DialogueGrantPacket</c>
/// whatsoever. This pump replaces that unreliable path with:
///
/// <b>Detect</b> — watch the SAME <c>PlayerState.EquippedRightWeapon</c>/<c>EquippedLeftWeapon</c>
/// fields already replicated every tick (they cannot miss an equip the way a possessed-items
/// diff can, because they ARE the equip slots, not a repository snapshot). The first tick either
/// slot shows one of the three canonical starter sabers, announce it.
///
/// <b>Deliver</b> — no new packet. The announcement is one session-wide, sticky flag
/// (<see cref="FirstLightsaberColorRule.FirstSaberBuiltFlagId"/>) on the existing world-progression
/// flag channel (<c>EventFlagStore</c>/<c>FlagOpPacket</c>) — already reliable, already merge-safe,
/// and already resynced whole to a late joiner via the paged world snapshot. No payload beyond
/// "it happened": every receiver (including the publisher's own next tick) decides ENTIRELY from
/// its own locally-read class which colour, if any, it personally needs.
///
/// <b>Apply</b> — while the flag reads true and this player has not yet equipped a first saber
/// (own weapon slots checked fresh every tick, never a one-shot latch trusted blindly — a resync/
/// rejoin/late join is naturally idempotent) and this player's own class list names a Jedi class,
/// queue exactly one automatic <c>equip</c> cheat — the SAME primitive
/// docs/research/companion-state-sync.md's own live-test verb already ships
/// (<c>CheatKinds.Equip</c> → <c>Odyssey.EquipItemFromTemplate</c>: create + <c>ActionEquipItem</c>
/// in one call, matching retail's own idiom) — for the resref that class implies.
///
/// <b>Equipping, not just giving, is deliberate.</b> An unequipped item sitting in a player's own
/// backpack IS the shared party pool on a live PC
/// (docs/research/key-items-and-solo.md §1: <c>CSWSCreature::GetItemRepository(1)</c> on a party
/// member is the pool) and would relay right back onto every OTHER connected player as pool
/// content — equipping removes the item from that repository the same call, exactly the property
/// that already protects the BUILDER's own saber from ever being seen as a pool addition.
/// </summary>
internal sealed class FirstLightsaberBuildPump(
    IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    private readonly Lock _pendingGate = new();

    private string? _pendingBuildResref;

    private bool _hasPublishedLocally;

    private bool _hasFirstSaber;

    private bool _gaveUp;

    private int _attempts;

    private long _retryAtMs;

    public void Pump(in PlayerState local, bool changedModule, long moduleSettledAtMs)
    {
        if (!settings.FirstSaberColorSync)
        {
            return;
        }

        bool locallyEquipped = FirstLightsaberColorRule.IsCanonicalFirstSaber(local.EquippedRightWeapon)
            || FirstLightsaberColorRule.IsCanonicalFirstSaber(local.EquippedLeftWeapon);

        if (locallyEquipped)
        {
            _hasFirstSaber = true;
        }

        PublishIfJustBuilt(locallyEquipped);

        if (_hasFirstSaber || _gaveUp)
        {
            return;
        }

        TryBuildOwnSaberIfEligible(local, changedModule, moduleSettledAtMs);
    }

    private void PublishIfJustBuilt(bool locallyEquipped)
    {
        if (_hasPublishedLocally || !locallyEquipped)
        {
            return;
        }

        _hasPublishedLocally = true;

        if (session.Flags.TryGet(FirstLightsaberColorRule.FirstSaberBuiltFlagId, out sbyte already) && already != 0)
        {
            return; // already known to the party - nothing new to announce
        }

        log("first lightsaber: this character is wielding a starter lightsaber - announcing it to the party");
        session.SetFlag(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1);
    }

    private void TryBuildOwnSaberIfEligible(in PlayerState local, bool changedModule, long moduleSettledAtMs)
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

        long now = SessionClock.NowMs;
        if (now < _retryAtMs)
        {
            return;
        }

        // Same "mid our own transition" reading ClassGrantPump's own replay uses: the load
        // itself, the settle window after an arrival, a running conversation, or combat — never
        // a safe moment for an engine call aimed at this player's own equipment.
        bool midTransition = ModuleNameGuard.IsFallbackModuleName(local.Module)
            || (changedModule && now - moduleSettledAtMs < settings.ModuleSettleMs);

        if (ClassGrantReplayRule.Decide(local.Module, bridge.IsInDialogue, midTransition, local.IsInCombat)
            == ClassGrantReplayPath.Wait)
        {
            return;
        }

        lock (_pendingGate)
        {
            if (_pendingBuildResref is not null)
            {
                return; // one request already in flight - let it land before trying again
            }

            _attempts++;
            _retryAtMs = now + ClassGrantReplayRule.RetryMs;

            if (_attempts > ClassGrantReplayRule.MaxAttempts)
            {
                _gaveUp = true;
                log($"first lightsaber: giving up trying to build this character's own '{resref}' "
                    + $"after {_attempts - 1} attempt(s)");
                return;
            }

            log($"first lightsaber: building this character's own '{resref}' from the party's "
                + $"construction (attempt {_attempts}/{ClassGrantReplayRule.MaxAttempts})");
            _pendingBuildResref = resref;
        }
    }

    /// <summary>
    /// Called by whatever already arbitrates the single-slot staging-cheat channel (the command
    /// dispatcher's pending-cheat-serial check), at most once per ITS tick — the same discipline
    /// <c>LootPump.TryDequeuePendingLoot</c> already follows for its own automatic give.
    /// </summary>
    public bool TryDequeuePendingBuild(out string resref)
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
