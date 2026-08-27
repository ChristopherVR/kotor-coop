namespace KotorCoop.Plugin;

// The effective-behaviour-flags channel: which of the five PLUGIN-gated coop.ini keys
// (effect_sync, trigger_relay, liveness_sync, map_reveal_sync, dialogue_recruit_sync)
// KotorCoop.Session's host-authoritative comparison overrode for this session, and what
// value each was overridden to. See SharedBlockView.cs for the class declaration,
// BehaviourOverrideRule.cs for the pure decision this feeds, and EffectiveBehaviourFlag.cs
// for the read-and-log-on-change wrapper each of the five controllers uses.
internal partial struct SharedBlock
{
    // ---- effective behaviour-override flags (version 55, region 0x40C0..0x40C8, no
    // TotalSize growth) -----------------------------------------------------------------
    //
    // docs/research/mod-compatibility.md's "Behaviour-flag negotiation" gap, closed
    // 2026-08-27: KotorCoop.Session.SessionOverrides already computes the adopted value for
    // these five keys the moment a client's BehaviourDigest comparison against the host's
    // Welcome finds a host-authoritative difference, but nothing carried that into this
    // SEPARATE process — every one of the five controllers below only ever read its own
    // PluginConfig/coop.ini value, once, at construction. This region is the missing wire.
    //
    // Two packed dwords rather than five separate bytes, the same concurrency reasoning
    // OffsetLocalPartyRoster's own comment gives: a single aligned dword store on x86 cannot
    // be torn, so a reader never observes half of one session's overrides mixed with half of
    // another's. Bit N of OffsetBehaviourOverrideMask set = "the host's value for that key
    // must win over whatever PluginConfig says"; the paired bit N of
    // OffsetBehaviourOverrideValues is that value. A cleared mask (0 — what a fresh block
    // reads before any agent has ever written it, and what SessionScopedBlockLevels resets it
    // to on disconnect) means every key defers to PluginConfig exactly as it always did; the
    // plugin needs no separate notion of "is a session connected" for this region at all.
    //
    // Placed in the 0x40 free bytes the companion-equipment array (version 54,
    // 0x4000..0x40C0) leaves before TotalSize (0x4100) — the same "starts cleanly in the free
    // tail, no TotalSize growth" choice versions 47/48/50/51/52 already made.

    /// <summary>Agent -&gt; plugin. Bit N set = a host-authoritative comparison overrode key N
    /// for this session — see <see cref="OverrideBitEffectSync"/> and its four siblings for
    /// the bit assignment. Cleared to 0 on disconnect (see
    /// <c>KotorGameBridge.SessionScopedBlockLevels</c>).</summary>
    public const int OffsetBehaviourOverrideMask = 0x40C0;

    /// <summary>Agent -&gt; plugin. The value each overridden key was overridden TO, one bit
    /// per key, meaningful only where the paired bit of <see cref="OffsetBehaviourOverrideMask"/>
    /// is set.</summary>
    public const int OffsetBehaviourOverrideValues = 0x40C4;

    // ⚠ KEEP IN SYNC with KotorCoop.Memory.BehaviourOverrideBits. The two halves of this mod
    // cannot share a type — the plugin is NativeAOT win-x86 and the agent is x64 — so this
    // table is written twice on purpose, exactly like SharedBlock.CheatKind*/CheatKinds.
    // BehaviourOverrideBitsSyncTests.cs cross-checks the two the same way CheatKindSyncTests.cs
    // already does for the cheat table.

    public const uint OverrideBitEffectSync = 1;
    public const uint OverrideBitTriggerRelay = 2;
    public const uint OverrideBitLivenessSync = 4;
    public const uint OverrideBitMapRevealSync = 8;
    public const uint OverrideBitDialogueRecruitSync = 16;
}
