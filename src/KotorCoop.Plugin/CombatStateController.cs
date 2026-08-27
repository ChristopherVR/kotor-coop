namespace KotorCoop.Plugin;

/// <summary>
/// RETIRED 2026-08-27 (docs/roadmap-seamless.md Tier 2 item 17;
/// docs/research/combat-participation.md's dated section has the full evidence trail).
///
/// This class used to re-assert the local PC's in-combat dword (<c>CSWSCreature+0x4E0</c>)
/// directly through the engine's own setter every ~60 ms, to make a client's HUD, combat
/// music, auto-pause and round timing agree that a fight was happening even though its own
/// engine was only mirroring. Measured live 2026-08-11: a written flag on a real player with
/// no attackers is reverted by the engine in 0–2 ms — the field is not a latch the engine
/// consults, it is continuously RE-DERIVED from whether the creature is actually being
/// fought (a second writer sits inside <c>CSWSCreature::ProcessPendingCombatActions</c> at
/// <c>0x004F7C0E</c>, past the one <c>SetCombatState</c> itself performs). Re-asserting on
/// any cadence a frame hook can manage held the field for ~2 ms out of every 60 — it never
/// worked, and it was shipped off by default for exactly that reason.
///
/// This class's own remarks used to describe THE REAL FIX as: stop clearing the
/// attack-permitted bit on enemies near the local player, so its own copies genuinely swing
/// at it and its combat state rises for the same reason the host's does. That fix has since
/// shipped as <c>local_combat</c> (<c>EnemyPresenceController</c>,
/// docs/research/shared-state-audit.md round 39, ON by default) — pulling one genuinely
/// engaging enemy out of the census and firing its own event script through the engine's
/// real dispatcher gives it (and, once a swing lands, this player) an ACTUAL combat round.
/// Round 52 (2026-08-21, the same document) live-verified the direct consequence this class
/// exists to have chased: <c>this engine says in-combat=True</c> was observed on the client's
/// own engine while a ground-truth-confirmed real local hit was landing —
/// <c>combat_state_sync</c> never having been enabled in that session. The real fix already
/// delivers what this class's dword poke never could, with no help from it.
///
/// So there is nothing left for a direct write to do: no cheaper engine call was found either
/// (the symbol database has no distinct "enter combat" routine — <c>CSWSCreature::SetCombatMode</c>
/// (<c>0x0050EE80</c>) is the weapon-draw/animation-mode switch used on target-loss, an
/// entirely different field, not the in-combat dword this class chased). This class stays only
/// so <c>combat_state_sync</c> keeps parsing without a config-loader error; it performs no
/// write, no engine call, and holds no state. <see cref="Odyssey.SetPlayerCombatState"/> is
/// kept, unused, for the same reason <c>Odyssey.QueueAttackAction</c> was kept after its own
/// retirement — a verified engine binding worth keeping documented rather than deleted, should
/// a future round ever need it again.
/// </summary>
internal sealed class CombatStateController
{
    private readonly bool _configured = PluginConfig.ReadBool("combat_state_sync", false);
    private bool _deprecationLogged;

    /// <summary>Nothing to release — this class no longer holds or writes any engine state.</summary>
    public void Invalidate()
    {
    }

    public void OnFrame()
    {
        if (!_configured || _deprecationLogged)
        {
            return;
        }

        _deprecationLogged = true;
        PluginHost.Log(
            "combat_state_sync: retired 2026-08-27 - superseded by local_combat's own real "
            + "fight arming (docs/research/combat-participation.md); this key is now a no-op");
    }
}
