namespace KotorCoop.Protocol;

/// <summary>
/// The slice of a player's avatar that is replicated every tick. This is deliberately
/// small: it is the "phantom body" layer, equivalent to what FromSoftware's netcode
/// already replicates for summoned phantoms. World progression is a separate channel
/// (see <see cref="FlagOpPacket"/>).
/// </summary>
public readonly record struct PlayerState(
    Vector3f Position,
    float Facing,
    string Module,
    int Health,
    uint Animation,
    uint Appearance = 0,
    int MaxHealth = 0,
    string EquippedBody = "",
    string EquippedRightWeapon = "",
    string EquippedLeftWeapon = "",
    uint Flags = 0,
    ushort OneShotAnimation = 0,
    byte OneShotSequence = 0)
{
    // ⚠ <see cref="OneShotAnimation"/> is an EDGE, not a level. Weapon draws, attack swings,
    // flourishes and item use are fire-and-forget: they finish between two ticks and some
    // never touch an object field at all, so they cannot be sampled — the plugin captures
    // them by hooking the engine's dispatch and carries a sequence alongside, which is what
    // makes a repeated identical flourish replicate instead of being swallowed as "no
    // change". See docs/research/animation-and-minimap.md.

    /// <summary>The player is in a conversation. Enemy publication freezes for them, and
    /// peers' UIs can say "talking" instead of showing a statue.</summary>
    public const uint FlagInDialogue = 1;

    /// <summary>The player has the game paused (their own pause, not one mirrored from a
    /// peer — mirrored pauses are excluded at the source or two instances would latch
    /// each other paused forever).</summary>
    public const uint FlagPaused = 2;

    /// <summary>The player's character is in combat (creature +0x4E0).</summary>
    public const uint FlagInCombat = 4;

    /// <summary>
    /// The player is DOWNED — at zero hit points and in the engine's "temp dead" state,
    /// which party members enter instead of dying. They are not out of the session: the
    /// engine stands them up once the area is clear, and a peer who reaches them can revive
    /// them sooner. Rides the existing per-tick state, so no extra channel.
    /// </summary>
    public const uint FlagDowned = 8;

    /// <summary>
    /// This player has just RELEASED a shared pause they did not start.
    ///
    /// Needed because a release is not observable as the absence of <see cref="FlagPaused"/>:
    /// a mirroring player never had that flag set in the first place (mirrored pauses are
    /// excluded at the source), so "peer is not paused" is indistinguishable from "peer has
    /// unpaused us both". Without this bit only the ORIGINATOR could end a shared pause, and
    /// the other player pressing pause unfroze their own game while their partner stayed
    /// frozen — reported from play, and reproduced: instance 1 held its pause after instance
    /// 2 released.
    ///
    /// An EDGE published as a level for a short window, like the one-shot animations: a
    /// release that is lost is lost forever, while a couple of ticks of repetition converges.
    /// </summary>
    public const uint FlagPauseReleased = 16;

    /// <summary>
    /// The player is inside the engine's own minigame mode: a swoop race or a forced turret
    /// sequence (<c>CSWCModule::GetMiniGame</c>, docs/research/minigames.md). They are not
    /// meaningfully in the shared world for the duration — a peer's instance parks their
    /// phantom instead of mirroring it, and this instance stops applying forwarded damage
    /// and incoming phantom writes while the flag is set (coop.ini <c>minigame_hide_peer</c>
    /// / the receiving side always gates the damage half).
    /// </summary>
    public const uint FlagInMinigame = 32;

    /// <summary>
    /// The player is AWAY: <c>CoopAgent.PumpOnce</c> has failed to read their local state for
    /// <see cref="KotorCoop.Session.AwayRule.ConsecutiveFailuresThreshold"/> consecutive ticks
    /// — the main menu, a load screen, the character/export screen, or anywhere else there is
    /// no live player object to sample. Set on the last known state and kept published every
    /// tick while the drought continues, so peers do not merely stop hearing from this player
    /// (indistinguishable from ordinary jitter) but are told why: a peer's instance treats an
    /// away flag the same way it already treats <see cref="FlagInMinigame"/> — the phantom is
    /// parked rather than mirrored, since a stale position is worse than none. Cleared the
    /// instant a read succeeds again. See docs/research/disconnect-reconnect.md.
    /// </summary>
    public const uint FlagAway = 64;

    /// <summary>
    /// The player is the OWNER of a story-forced solo segment currently in progress —
    /// <c>KotorCoop.Protocol.SoloSegmentMachine</c>, gated to
    /// <c>KotorCoop.Protocol.SoloSegmentCatalogue</c>'s modules (the Star Forge's Bastila and
    /// Malak duels — docs/research/star-forge-finale.md §3.3/§3.5). The product decision
    /// recorded there: this is the OWNER's fight; the other human SPECTATES rather than
    /// entering the 1-on-1. A peer seeing this flag set, while standing in the same module,
    /// parks its own input, points its camera at the owner's phantom
    /// (<c>SpectateController</c>'s follow reused) and stops crediting its own damage against
    /// the owner's enemy mirror. This instance, while its OWN copy of the flag is set, hides
    /// every remote peer from what it writes locally — <c>SoloSegmentRule.ShouldHideAllPeers</c>
    /// — so the duel script never sees a second body. coop.ini <c>solo_segment_spectate</c>
    /// (agent-side key, default ON — live-verified 2026-08-28,
    /// docs/research/star-forge-finale.md §12).
    ///
    /// TODO(version): this is a new wire bit on an existing dword (no packet shape changed),
    /// so nothing here requires a <c>ProtocolConstants.Version</c> bump by itself — flag it at
    /// merge time in case the reviewer wants one anyway for the changelog.
    /// </summary>
    public const uint FlagSoloSegment = 128;

    /// <summary>
    /// This player's WHOLE PARTY is down: their engine asked for the game-over screen (which
    /// the plugin swallowed while the session decides). Strictly stronger than
    /// <see cref="FlagDowned"/>, which only says the player character itself is on the floor —
    /// a host whose PC is down while a companion still fights is downed but NOT wiped, and
    /// that companion may yet win the fight and stand everybody back up.
    ///
    /// Live 2026-09-02 (docs/research/death-handling.md §6.4): judging a wipe on
    /// <see cref="FlagDowned"/> alone sent a client to the load-game menu while the host's
    /// Trask was still standing and the host's PC then auto-revived and played on — one
    /// player at a menu, the other mid-fight. A wipe is unanimous only when every
    /// participant's party is gone, and the engine's own game-over ask is exactly that signal
    /// per instance. A LEVEL: set on the first swallowed ask of a downed episode, cleared
    /// when the PC stands up. Derived agent-side from the plugin's game-over serial (no block
    /// layout change).
    /// </summary>
    public const uint FlagPartyWiped = 256;

    /// <summary>True while this player is asking to end a shared pause they did not start.</summary>
    public bool HasReleasedPause => (Flags & FlagPauseReleased) != 0;

    public static PlayerState Empty { get; } = new(Vector3f.Zero, 0f, string.Empty, 0, 0);

    /// <summary>Rejects NaN/infinite reads (a bad offset profile) AND finite out-of-world ones
    /// (a creature read mid-teardown) — see Vector3f.IsWorldPlausible for the live hang.</summary>
    public bool IsPlausible => Position.IsWorldPlausible && float.IsFinite(Facing);

    public bool IsInDialogue => (Flags & FlagInDialogue) != 0;

    public bool IsPaused => (Flags & FlagPaused) != 0;

    public bool IsInCombat => (Flags & FlagInCombat) != 0;

    public bool IsDowned => (Flags & FlagDowned) != 0;

    public bool IsPartyWiped => (Flags & FlagPartyWiped) != 0;

    public bool IsInMinigame => (Flags & FlagInMinigame) != 0;

    public bool IsAway => (Flags & FlagAway) != 0;

    public bool IsInSoloSegment => (Flags & FlagSoloSegment) != 0;
}

/// <summary>A remote player as seen locally, after interpolation.</summary>
public sealed record RemotePeer(
    uint PeerId,
    string Name,
    PlayerState State)
{
    /// <summary>
    /// True when the peer is in the same module (area) as the local player. KOTOR loads
    /// one module at a time, so a peer elsewhere must not be drawn.
    /// </summary>
    public bool IsInModule(string module) =>
        string.Equals(State.Module, module, StringComparison.OrdinalIgnoreCase);
}
