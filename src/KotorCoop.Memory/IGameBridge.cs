// The seam between the co-op stack and a specific game. Everything above this interface
// is game-agnostic; everything game-specific is an offset profile behind it. Split into
// role interfaces (one per concern, each carrying its own default implementations) so a
// bridge or a test fake can be read one concern at a time; this file is just the union.
//
// CheatKinds stays HERE rather than moving to its own file: CheatKindSyncTests.cs (an
// existing test this pass may not edit) reads this exact file's text to find every
// `public const uint` and compare it against the plugin's SharedBlock.CheatKind* table.
namespace KotorCoop.Memory;

/// <summary>
/// The cheat kinds, mirroring the plugin's <c>SharedBlock.CheatKind*</c> constants.
///
/// ⚠ KEEP IN SYNC with SharedBlock. The two halves of this mod cannot share a type — the
/// plugin is NativeAOT win-x86 and the agent is x64 — so every block constant is written
/// twice on purpose, exactly like the travel, journal and browser offsets above it. A wrong
/// number here means the plugin runs a different verb from the one that was typed.
///
/// Per-value history (which round added which number and why) lives in
/// docs/research/shared-state-audit.md; only the current, load-bearing "next free number"
/// warning is kept inline below.
/// </summary>
public static class CheatKinds
{
    public const uint God = 1;
    public const uint TopUp = 2;
    public const uint StandUp = 3;
    public const uint Slay = 4;
    public const uint Teleport = 5;
    public const uint DropItem = 6;

    /// <summary>Stage a cinematic onto a module transition. <c>Amount</c> is the mode
    /// (<c>StageMovieModes</c>) and <c>Text</c> the comma-separated movie names.</summary>
    public const uint StageMovie = 7;

    /// <summary>Read, and optionally poke, one live item's per-instance identity — the
    /// staging cheat that makes the ground channel's apply path testable without a human.
    /// Mirrors <c>SharedBlock.CheatKindSetItem</c>.</summary>
    public const uint SetItem = 8;

    /// <summary>"Leave this identity field alone." Mirrors
    /// <c>SharedBlock.CheatFieldUnchanged</c>; see there for why all-ones is safe.</summary>
    public const uint Unchanged = 0xFFFF_FFFF;

    /// <summary>Ask the plugin what this engine knows about one object id, in one frame, on
    /// the game thread. See SharedBlock.CheatKindResolve.</summary>
    public const uint Resolve = 9;

    /// <summary>
    /// Make the local player use a door, so the ENGINE starts a module transition and the
    /// plugin's pre-store flush window is the only thing between a phantom and the stored
    /// module. <c>Text</c> is the door tag, <c>Target</c> its object id; both empty means "the
    /// nearest module transition door". See SharedBlock.CheatKindUseDoor.
    /// </summary>
    public const uint UseDoor = 10;

    /// <summary>
    /// Gives a materialised enemy's local stand-in one item out of its real corpse's actual
    /// inventory. <c>Target</c> is the OWNER's enemy object id (not a local one — only the
    /// receiving instance's own materialisation table can translate it), <c>Text</c> the item
    /// resref, and Amount/Charges/MaxCharges/Upgrades/ItemFlags its identity, exactly as
    /// <see cref="SetItem"/> already carries for an existing item. See
    /// SharedBlock.CheatKindGiveLoot and docs/research/enemy-materialisation.md §5.
    /// </summary>
    public const uint GiveLoot = 11;

    /// <summary>
    /// Peeks (<c>Amount</c> 0) or fires (<c>Amount</c> nonzero) a creature's +0x260 event
    /// script through the engine's own RunScript dispatcher, instead of replicating
    /// combat-round mechanics natively. <c>Target</c> is the creature's object id. See
    /// SharedBlock.CheatKindFireScript and docs/research/shared-state-audit.md §2.
    /// </summary>
    public const uint FireScript = 12;

    /// <summary>
    /// TEMPORARY DIAGNOSTIC: snaps <c>Target</c> to a hardcoded position near END_M01AA's
    /// `sithsoldier 0xE`, so a genuinely non-census creature (a party member) has a valid
    /// in-range target to test +0x260 firing against. See SharedBlock.CheatKindMoveNear and
    /// docs/research/shared-state-audit.md §2. Remove once settled.
    /// </summary>
    public const uint MoveNear = 13;

    /// <summary>
    /// Runs one of the peer-effect channel's non-damage constructors directly. <c>Target</c>
    /// is the id to aim at (0 = the nearest phantom), <c>Amount</c> the effect kind (the
    /// <c>PeerEffectKind</c> numeric value, 1..9), <c>Charges</c> the duration in milliseconds
    /// and <c>MaxCharges</c> the magnitude — reused from the item-identity fields rather than
    /// new ones, exactly like <see cref="GiveLoot"/> reuses them for stack/charges/upgrades.
    /// See SharedBlock.CheatKindApplyEffect and docs/research/effect-sync.md.
    /// </summary>
    public const uint ApplyEffect = 14;

    /// <summary>
    /// TEST HOOK: plays <c>Text</c> (a bare resref) right now on this instance, through the
    /// same engine entry a mid-module <c>PlayMovie</c> call and the movie relay's own
    /// <c>PlayNow</c> path use. See SharedBlock.CheatKindMoviePlay and
    /// docs/research/cinematics.md §10.
    ///
    /// ⚠⚠ Live-verified to HANG the target instance indefinitely for real movies — see
    /// cinematics.md §10.8. Sending this cheat can require killing the game process.
    /// </summary>
    public const uint MoviePlay = 15;

    /// <summary>
    /// Destroys a creature outright through <c>Odyssey.DestroyObjectById</c> — the SAME engine
    /// call <c>LivenessReconcileController</c>'s own Destroy action already wraps
    /// (docs/research/creature-liveness.md §5), so this stages the one reconcile action that
    /// channel's live passes could never isolate: <c>slay</c> only ever calls
    /// <c>Odyssey.KillCreature</c>, never a destroy, so nothing before this verb could put a
    /// creature into the "Gone" state the liveness channel exists to catch on its own. Follows
    /// <see cref="ApplyEffect"/>'s wiring exactly (ConsoleCommand → dispatcher →
    /// CheatController → the engine call) and <c>slay</c>'s authority rules exactly (refuses a
    /// player, a phantom, a companion, a plot creature — see <c>CheatAuthority.ForDestroy</c>).
    /// <c>Target</c> is the object id; no tag form, no "nearest" fallback — the whole point is
    /// staging a SPECIFIC out-of-census id a `census` read already named.
    ///
    /// HOST-ONLY, deliberately smaller than <see cref="Slay"/>: a mirroring instance (this
    /// engine does not own the fight) is refused rather than forwarded — there is no
    /// destroy-ledger, and the creature-liveness channel's own job is to carry a destroy TO a
    /// mirroring instance, not to originate one from it.
    /// </summary>
    public const uint DestroyObject = 16;

    /// <summary>
    /// Opens an ORDINARY door (not a module transition — <c>usedoor</c> refuses those by
    /// design) through the engine's own door-use action. <c>Text</c> is the door tag,
    /// <c>Target</c> its object id; both empty means "the nearest door", with no
    /// linked-to-module filter. See SharedBlock.CheatKindOpenDoor and
    /// docs/research/shared-state-audit.md round 56/57.
    /// </summary>
    public const uint OpenDoor = 17;

    /// <summary>
    /// Makes the local player USE a placeable (container, corpse, switch) through the engine's
    /// own client-to-server input pipe, so the real OnUsed/GUI path runs — the container-sync
    /// live-test primitive. <c>Text</c> is the placeable's tag, <c>Target</c> its object id;
    /// both empty means "the nearest container placeable". See SharedBlock.CheatKindUsePlaceable
    /// and docs/research/container-sync.md.
    /// </summary>
    public const uint UsePlaceable = 18;

    /// <summary>
    /// Closes a placeable's loot panel through the engine's real <c>CloseInventory</c> call —
    /// the close-side counterpart to <see cref="UsePlaceable"/>, added when the container-sync
    /// live-verify pass found <c>useplaceable</c> alone gave no way to close what it opened
    /// (that verb only sends the network half of a real click; the client-side GUI-show code
    /// is deliberately not replicated, so there is no panel for a keystroke to close). <c>Text</c>
    /// is the placeable's tag, <c>Target</c> its object id; both empty means "the nearest
    /// container placeable". See SharedBlock.CheatKindClosePlaceable and
    /// docs/research/container-sync.md.
    /// </summary>
    public const uint ClosePlaceable = 19;

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE: switches the LOCAL player's controlled character to a
    /// party-roster companion, or back to their own PC — the reverse of
    /// <c>KotorGameBridge.LockControlToPlayer</c>'s per-tick revert, written in-process by the
    /// same id-only field. <c>Amount</c> is the party-roster slot (0-8), or -1 for "your own
    /// PC". Built so <c>control-return-live.ps1</c> can put a client onto a companion before
    /// connecting without the one manual step it could never automate. See
    /// SharedBlock.CheatKindLeader and docs/research/control-return.md.
    /// </summary>
    public const uint Leader = 20;

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE (docs/research/shared-state-audit.md, party-reconciliation
    /// round): forces NPC roster slot <c>Amount</c> (0-8) into the local active party — poke
    /// available, spawn if needed, add — without a real recruitment conversation. See
    /// SharedBlock.CheatKindRecruit.
    /// </summary>
    public const uint Recruit = 21;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/dialogue-skip-desync.md): forces the local player's
    /// OWNED conversation's current line/VO to finish immediately — the in-process equivalent
    /// of a human's retail skip keypress/click, built because injected OS input never reaches
    /// this game (docs/live-verification.md). No fields carried; the target is always
    /// "whatever conversation this instance owns right now". See SharedBlock.CheatKindSkipLine.
    /// </summary>
    public const uint SkipLine = 22;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/dialogue-skip-desync.md): commits reply slot
    /// <c>Amount</c> on the local player's OWNED conversation — the console's way to navigate
    /// past a reply-choice screen without real OS input, needed so the skip live-test can reach
    /// an actual narration stretch (skip is a no-op on a reply screen; see <c>SkipLine</c>).
    /// See SharedBlock.CheatKindSelectReply.
    /// </summary>
    public const uint SelectReply = 23;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/minigames.md): queues a real
    /// <c>ActionMoveToLocation</c> for the local player, through <c>Odyssey.MoveToLocation</c> —
    /// the same call <see cref="UseDoor"/>'s walk already uses, but to a bare point (<c>X</c>/
    /// <c>Y</c>/<c>Z</c>) rather than a door threshold. Built because a subarea trigger's
    /// <c>OnEnter</c> only fires for a MOVING creature (docs/research/trigger-relay.md §2) — a
    /// <see cref="Teleport"/> snap crosses nothing — and reaching a trigger polygon that has no
    /// door on it (a swoop-track entrance) had no walk primitive before this.
    ///
    /// ⚠ 24 is the highest number taken. Take the next FREE one for a new verb —
    /// CheatKindSyncTests.NoTwoCheatKindsShareANumber will fail the build on a collision.
    /// </summary>
    public const uint WalkTo = 24;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/disconnect-reconnect.md §9): tears the
    /// world down and shows the main menu, via the engine's own
    /// <c>CClientExoAppInternal::ShutDownToMainMenu</c> — the same function a real "Quit Game
    /// -&gt; Yes" click reaches. Built to exercise the Away flag's third shape (no player object
    /// at all) without navigating the vanilla pause menu by mouse, which a prior round found
    /// does not land clicks on a demo.ps1-repositioned window and once wedged an instance via
    /// blind keyboard navigation (docs/research/session-save.md). See
    /// <c>SharedBlock.CheatKindQuitToMenu</c>. Takes no arguments; local only.
    ///
    /// ⚠ 25 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint QuitToMenu = 25;
}

/// <summary>
/// The seam between the co-op stack and a specific game. Everything above this interface
/// is game-agnostic; everything game-specific is an offset profile behind it.
/// </summary>
public interface IGameBridge :
    IPlayerBridge,
    IWorldStateBridge,
    IGroundItemBridge,
    IPluginChannelBridge,
    IDoorBridge,
    IPlaceableBridge,
    IContainerLootBridge,
    IMenuBridge,
    ITravelBridge,
    IDialogueBridge,
    IPartyBridge,
    IEnemyBridge,
    IItemBridge,
    IEncounterBridge,
    ICheatBridge,
    IJournalBridge,
    IDisposable
{
}
