using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// One staging cheat, as the agent hands it to the plugin.
///
/// A flat record rather than a verb-per-method because the plugin side is a single
/// serial-edged slot in the shared block: adding a cheat is a new <paramref name="Kind"/>
/// constant on both sides, not a new channel. <paramref name="Text"/> carries whichever
/// string the kind needs (a creature tag, a blueprint resref) and is capped by the block's
/// own 32-byte field.
/// </summary>
public readonly record struct CheatRequest(
    uint Kind,
    uint Target = 0,
    int Amount = 0,
    float X = 0f,
    float Y = 0f,
    float Z = 0f,
    string Text = "",
    uint Charges = CheatKinds.Unchanged,
    uint MaxCharges = CheatKinds.Unchanged,
    uint Upgrades = CheatKinds.Unchanged,
    uint ItemFlags = CheatKinds.Unchanged);

/// <summary>
/// The cheat kinds, mirroring the plugin's <c>SharedBlock.CheatKind*</c> constants.
///
/// ⚠ KEEP IN SYNC with SharedBlock. The two halves of this mod cannot share a type — the
/// plugin is NativeAOT win-x86 and the agent is x64 — so every block constant is written
/// twice on purpose, exactly like the travel, journal and browser offsets above it. A wrong
/// number here means the plugin runs a different verb from the one that was typed.
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

    /// <summary>
    /// Ask the plugin what this engine knows about one object id, in one frame, on the game
    /// thread. See SharedBlock.CheatKindResolve.
    ///
    /// ⚠ 9, NOT 7: kinds 7 and 8 (StageMovie, SetItem) were taken by work developed in
    /// parallel. CheatKindsMatchTheBlockTests asserts this table against the plugin's, because
    /// a number that disagrees across the two halves runs a DIFFERENT VERB from the one typed.
    /// </summary>
    public const uint Resolve = 9;

    /// <summary>
    /// Make the local player use a door, so the ENGINE starts a module transition and the
    /// plugin's pre-store flush window is the only thing between a phantom and the stored
    /// module. <c>Text</c> is the door tag, <c>Target</c> its object id; both empty means "the
    /// nearest module transition door". See SharedBlock.CheatKindUseDoor.
    ///
    /// ⚠ 10, because 7, 8 and 9 are taken. Take the next FREE number.
    /// </summary>
    public const uint UseDoor = 10;

    /// <summary>
    /// Gives a materialised enemy's local stand-in one item out of its real corpse's actual
    /// inventory. <c>Target</c> is the OWNER's enemy object id (not a local one — only the
    /// receiving instance's own materialisation table can translate it), <c>Text</c> the item
    /// resref, and Amount/Charges/MaxCharges/Upgrades/ItemFlags its identity, exactly as
    /// <see cref="SetItem"/> already carries for an existing item. See
    /// SharedBlock.CheatKindGiveLoot and docs/research/enemy-materialisation.md §5.
    ///
    /// ⚠ 11, because 7-10 are taken. Take the next FREE number.
    /// </summary>
    public const uint GiveLoot = 11;

    /// <summary>
    /// STRATEGY 2 DIAGNOSTIC (2026-08-21): peek (<c>Amount</c> 0) or fire (<c>Amount</c>
    /// nonzero) a creature's +0x260 event script through the engine's own RunScript
    /// dispatcher, instead of replicating combat-round mechanics natively. <c>Target</c> is
    /// the creature's object id. See SharedBlock.CheatKindFireScript and
    /// docs/research/shared-state-audit.md §2, "Strategy 2."
    ///
    /// ⚠ 12, because 7-11 are taken. Take the next FREE number.
    /// </summary>
    public const uint FireScript = 12;

    /// <summary>
    /// ROUND 36 TEMPORARY DIAGNOSTIC: snaps <c>Target</c> to a hardcoded position near
    /// END_M01AA's `sithsoldier 0xE`, so a genuinely non-census creature (a party member) has a
    /// valid in-range target to test +0x260 firing against. See SharedBlock.CheatKindMoveNear
    /// and docs/research/shared-state-audit.md §2, round 36. Remove once settled.
    ///
    /// ⚠ 13, because 7-12 are taken. Take the next FREE number.
    /// </summary>
    public const uint MoveNear = 13;

    /// <summary>
    /// Runs one of the peer-effect channel's non-damage constructors directly. <c>Target</c>
    /// is the id to aim at (0 = the nearest phantom), <c>Amount</c> the effect kind (the
    /// <c>PeerEffectKind</c> numeric value, 1..9), <c>Charges</c> the duration in milliseconds
    /// and <c>MaxCharges</c> the magnitude — reused from the item-identity fields rather than
    /// new ones, exactly like <see cref="GiveLoot"/> reuses them for stack/charges/upgrades.
    /// See SharedBlock.CheatKindApplyEffect and docs/research/effect-sync.md.
    ///
    /// ⚠ 14, because 7-13 are taken. Take the next FREE number.
    /// </summary>
    public const uint ApplyEffect = 14;

    /// <summary>
    /// TEST HOOK: plays <c>Text</c> (a bare resref) right now on this instance, through the
    /// same engine entry a mid-module <c>PlayMovie</c> call and the movie relay's own
    /// <c>PlayNow</c> path use. See SharedBlock.CheatKindMoviePlay and
    /// docs/research/cinematics.md §10.
    ///
    /// ⚠⚠ Live-verified (2026-08-22) to HANG the target instance indefinitely for real movies —
    /// see cinematics.md §10.8. Sending this cheat can require killing the game process.
    ///
    /// ⚠ 15, because 7-14 are taken. Take the next FREE number.
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
    ///
    /// ⚠ 16, because 7-15 are taken. Take the next FREE number.
    /// </summary>
    public const uint DestroyObject = 16;
}

/// <summary>The plugin's verdicts, mirroring <c>SharedBlock.CheatStatus*</c>.</summary>
public static class CheatStatus
{
    public const int Applied = 1;
    public const int Forwarded = 2;
    public const int NoWorld = -1;
    public const int NoTarget = -2;
    public const int Refused = -3;
    public const int EngineRefused = -4;
}

/// <summary>
/// The seam between the co-op stack and a specific game. Everything above this interface
/// is game-agnostic; everything game-specific is an offset profile behind it.
/// </summary>
public interface IGameBridge : IDisposable
{
    string Description { get; }

    bool IsAttached { get; }

    /// <summary>
    /// False when the profile has no remote slot layout. The session still works: the
    /// local player is broadcast and remote players are tracked, there is simply nowhere
    /// in the game to draw them yet.
    /// </summary>
    bool CanWriteRemotes { get; }

    bool TryReadLocalState(out PlayerState state);

    /// <summary>
    /// The module the world is in RIGHT NOW, read directly from the engine — not retained
    /// from an earlier successful read the way <see cref="TryReadLocalState"/>'s caller
    /// retains a whole state across a failed one. Callers that label world-scoped data
    /// (the enemy census) must label it with this, read in the same breath as the data:
    /// a retained label and a live payload can belong to different worlds around a module
    /// transition. False when the bridge has no module reading; callers then fall back to
    /// the retained state's module, which is the pre-existing behaviour.
    /// </summary>
    bool TryReadCurrentModule(out string module)
    {
        module = string.Empty;
        return false;
    }

    bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers);

    /// <summary>
    /// Tears down the in-game co-op state on disconnect: no remote peers, no phantoms, the
    /// party freeze released. So a disconnected player is left in a clean single-player
    /// game rather than staring at a frozen phantom of someone who left.
    /// </summary>
    void ClearCoopState() { }

    /// <summary>
    /// False when this bridge has no way to materialise ground items in the game. The
    /// defaults make item sync purely opt-in so profile bridges need no changes.
    /// </summary>
    bool SupportsGroundItems => false;

    /// <summary>
    /// Declares the desired set of items on the ground; the bridge reconciles the game
    /// toward it (spawn what is missing, despawn what is no longer wanted).
    /// </summary>
    bool TryWriteGroundItems(IReadOnlyList<GroundItem> items) => false;

    /// <summary>
    /// Item ids the game reports as locally vanished (picked up by this player) since the
    /// last call. The caller announces these to the session.
    /// </summary>
    List<uint> CollectVanishedItems() => [];

    /// <summary>
    /// A digest of the global-variable catalogue (0 when unresolved or unsupported).
    /// Exchanged in-world so two saves whose catalogues differ — whose (type, index) keys
    /// would name different variables — do not silently corrupt each other's quest state.
    /// </summary>
    uint GlobalsCatalogueDigest => 0;

    /// <summary>
    /// A digest of the identity of the save this session is playing — its module, its
    /// Mod_NextObjId counters and its stored GIT object-id list (0 when unknown). Object
    /// ids are only identical across instances because both engines dealt them from the
    /// same save, so this is what the enemy/door/item channels' id assumption rests on.
    /// </summary>
    uint SaveIdentityDigest => 0;

    /// <summary>
    /// A digest of the object ids the named module's GIT hands out — from the save's
    /// stored module state when this save has visited it, else predicted from the pristine
    /// module file the engine would load (0 when unknown). Judged per module, because a
    /// mod that edits one module's placements shifts ids there and nowhere else.
    /// </summary>
    uint ModuleGitDigest(string module) => 0;

    /// <summary>
    /// Roadmap item 14 phase 2's load-detection edge: the plugin's own count of world
    /// teardowns it judged UNANNOUNCED — a Load Game action, not an ordinary module
    /// transition. See <c>KotorCoop.Plugin.WorldLoadRule</c> for the primitive and
    /// <c>SharedBlock.OffsetWorldLoadSerial</c> for the wire field (block version 49). 0 for
    /// bridges without an in-process plugin (the mock harness) — indistinguishable from "no
    /// loads observed yet", which is the correct default for either case.
    /// </summary>
    uint WorldLoadSerial => 0;

    /// <summary>
    /// Roadmap item 14 phase 2's rejoin action: requests the plugin replay the same adopted-
    /// world load <c>agent adopt</c> uses for the first join. False (with a reason) for
    /// bridges that cannot drive the plugin at all (the mock harness).
    /// </summary>
    bool TryRequestAdoptedWorldLoad(out string diagnostic)
    {
        diagnostic = "no in-process plugin to request a load from";
        return false;
    }

    /// <summary>Polls the outcome of the last <see cref="TryRequestAdoptedWorldLoad"/>.</summary>
    bool TryGetAdoptedWorldLoadStatus(out uint applied, out int status)
    {
        applied = 0;
        status = 0;
        return false;
    }

    /// <summary>Damage a peer's phantom has taken in this game, as a running total. Default
    /// "nothing recorded" for bridges without an in-process plugin (the mock harness).</summary>
    bool TryReadPhantomHit(int slot, out uint phantomId, out uint cumulative, out uint attacker)
    {
        phantomId = 0;
        cumulative = 0;
        attacker = 0;
        return false;
    }

    /// <summary>Hands the plugin the running total this player owes for hits taken on their
    /// phantom elsewhere. No-op without a plugin.</summary>
    bool TryWriteIncomingDamage(uint cumulative, uint attacker) => false;

    /// <summary>Non-damage effects (stun, paralyze, knockdown, slow, frightened, sleep, choke,
    /// Force push/whirlwind) captured landing on this instance's phantoms since the plugin
    /// last published them. Empty for bridges without an in-process plugin. See
    /// docs/research/effect-sync.md.</summary>
    List<PeerEffectRecord> ReadPeerEffectsOut() => [];

    /// <summary>Hands the plugin one effect a peer's hook captured landing on this player's
    /// phantom over there, for it to replay on the real character here. No-op without a
    /// plugin.</summary>
    bool TryWritePeerEffectIn(uint originPeerId, uint kind, uint durationMs, uint magnitude, uint serial) => false;

    /// <summary>How many remote-player slots this bridge publishes into.</summary>
    int RemoteSlotCapacity => 0;

    /// <summary>
    /// Which peer owns a published slot, 0 for empty.
    ///
    /// ⚠ Read this rather than assuming "slot i is the i'th peer in the list you handed
    /// over". It is not: the list is filtered by module and masked by capability before it
    /// is written, and slots are sticky per peer so a disconnect does not renumber the
    /// survivors. Attributing a phantom's damage by list position instead of by this map
    /// bills the wrong player, and with three of them that is the normal case, not the
    /// edge case.
    /// </summary>
    uint PeerInSlot(int slot) => 0;

    /// <summary>False when this bridge cannot see the game's world/quest state.</summary>
    bool SupportsWorldState => false;

    /// <summary>
    /// World-state variables that changed locally since the last call, as (flag id, value).
    /// These are the quest and story booleans and counters the whole game branches on.
    /// </summary>
    List<KeyValuePair<uint, sbyte>> CollectChangedWorldState() => [];

    /// <summary>
    /// Applies a peer's world-state value. Must also update the bridge's own shadow copy,
    /// or the next scan sees it as a local change and broadcasts it straight back.
    /// </summary>
    bool TryApplyWorldState(uint flagId, sbyte value) => false;

    /// <summary>False when this bridge cannot see or drive door state.</summary>
    bool SupportsDoorSync => false;

    /// <summary>
    /// Door states that advanced locally since the last call (opened, unlocked,
    /// destroyed), as door-typed world flags ready for the reliable flag channel.
    /// </summary>
    List<KeyValuePair<uint, sbyte>> CollectChangedDoorState() => [];

    /// <summary>
    /// Publishes the session's door truth for the local module into the game. The bridge
    /// filters: flags for doors in other modules are held back until that module loads.
    /// </summary>
    bool TryWriteDoorStates(IReadOnlyList<KeyValuePair<uint, sbyte>> flags) => false;

    /// <summary>False when this bridge cannot see or drive placeable (chest/corpse) state.</summary>
    bool SupportsPlaceableSync => false;

    /// <summary>
    /// Placeable states that advanced locally since the last call (opened, unlocked), as
    /// placeable-typed world flags ready for the reliable flag channel.
    /// </summary>
    List<KeyValuePair<uint, sbyte>> CollectChangedPlaceableState() => [];

    /// <summary>Publishes the session's placeable truth for the local module into the game.</summary>
    bool TryWritePlaceableStates(IReadOnlyList<KeyValuePair<uint, sbyte>> flags) => false;

    /// <summary>False when this bridge cannot see or drive container (chest/corpse) contents.</summary>
    bool SupportsContainerLoot => false;

    /// <summary>
    /// Symmetric on both host and client: items that left a shared container on THIS instance
    /// since the last call, as (container, item) object-id pairs. The caller announces these so
    /// the same item is removed from every other instance's copy of the container and cannot be
    /// taken twice.
    /// </summary>
    List<ContainerLootRemoval> CollectContainerLootRemovals() => [];

    /// <summary>
    /// Symmetric on both host and client: declares the merged container-loot removals the
    /// session knows about (this instance's own plus every peer's), which the bridge applies to
    /// the game's own containers (deleting each looted item). Idempotent.
    /// </summary>
    bool TryWriteContainerLoot(IReadOnlyList<ContainerLootRemoval> removals) => false;

    /// <summary>False when this bridge cannot mirror a peer's pause into the game.</summary>
    bool SupportsPauseSync => false;

    /// <summary>
    /// Holds (or releases) a pause on the peers' behalf. Either player pausing pauses
    /// both worlds — anything else lets one side act in a world the other believes is
    /// frozen.
    /// </summary>
    bool TrySetRemotePause(bool desired) => false;

    /// <summary>False when this bridge cannot replicate combat state into the game.</summary>
    bool SupportsCombatSync => false;

    /// <summary>
    /// Declares that a REMOTE peer standing in this player's module is fighting, so the
    /// plugin can put this engine into combat too. A client's own enemies are mirrors with
    /// the attack bit cleared, so its engine never works this out for itself — and combat
    /// state gates round timing, the HUD, auto-pause and how abilities queue.
    /// </summary>
    bool TrySetSessionInCombat(bool fighting) => false;

    /// <summary>The plugin's count of local releases of a MIRRORED pause. A change is the
    /// release; the absolute value means nothing.</summary>
    uint LocalPauseReleaseSerial => 0;

    /// <summary>Tells the plugin a peer ended the shared pause, so this instance drops the
    /// pause it started. Either player may end a shared pause.</summary>
    bool TrySignalRemotePauseRelease(uint serial) => false;

    /// <summary>False when this bridge cannot warp the local player between modules.</summary>
    bool SupportsTravel => false;

    /// <summary>
    /// Sends the local player to another module through the game's own transition path.
    /// This is what keeps a client in the host's module — travelling together is the
    /// difference between shared co-op and two parallel campaigns.
    /// </summary>
    /// <param name="movies">
    /// Movie resrefs to queue on the engine's transition list immediately before the warp, so
    /// a follower plays the same cinematic the owner did. Null or empty for the ordinary case.
    /// Written BEFORE the request serial, like the module and the waypoint — and the count is
    /// written even when there are none, so a previous request's movies can never ride along
    /// with this one.
    /// </param>
    bool TryRequestTravel(string module, string waypoint = "", IReadOnlyList<string>? movies = null) => false;

    /// <summary>
    /// The movie resrefs this instance's own imminent transition is about to play, with the
    /// serial the plugin stamped them with, or an empty list when it has announced none.
    ///
    /// A LEVEL plus a serial rather than a queue: the plugin gets exactly one frame to write
    /// this (the module load drains the engine's queue, and a playing movie then takes its
    /// main thread away entirely), so the agent must be able to read it late and still be
    /// right. De-duplicate on the serial.
    /// </summary>
    bool TryReadTransitionMovies(out uint serial, out string[] movies)
    {
        serial = 0;
        movies = [];
        return false;
    }

    /// <summary>
    /// The destination of this instance's own imminent module transition — module and entry
    /// waypoint — with the serial the plugin stamped it with when its engine committed to it,
    /// or false when it has announced none.
    ///
    /// The faster, earlier sibling of <see cref="TryReadTransitionMovies"/>: this is knowable
    /// before the load starts (a door's fade window, or the frame immediately before a
    /// plugin-initiated warp), while the movie queue only fills once the destination is
    /// already committed. A LEVEL plus a serial, same shape as the movie channel and for the
    /// same reason: the plugin gets one frame to write this before the load — or the movie —
    /// takes over, so the agent must be able to read it late and still be right.
    /// </summary>
    bool TryReadTransitionAnnounce(out uint serial, out string module, out string waypoint)
    {
        serial = 0;
        module = string.Empty;
        waypoint = string.Empty;
        return false;
    }

    /// <summary>
    /// Arms (or disarms) the plugin's refusal of a CLIENT-initiated real door transition —
    /// coop.ini <c>client_door_refusal</c>. While armed, this instance's own engine-native
    /// door crossing (as opposed to a session-commanded warp, which never touches this path)
    /// is refused early, before the load: the walk-through is cancelled and a line is posted
    /// naming the door, its destination, and (2026-08-22) WHY —
    /// <see cref="ClientDoorRefusalReason.HostMustLead"/> under <c>follow_lead=host</c> (today's
    /// exact behaviour) or <see cref="ClientDoorRefusalReason.HostBusy"/> under
    /// <c>follow_lead=any</c> when the host cannot safely follow right now. A LEVEL, computed
    /// and re-sent by the agent every tick from role, settings and whether the host has been
    /// seen transitioning recently — the plugin has no notion of "host" or "client" on its own.
    /// <see cref="ClientDoorRefusalReason.None"/> means not armed. Default false when this
    /// bridge has no plugin to arm.
    /// </summary>
    bool TryArmClientDoorRefusal(ClientDoorRefusalReason reason) => false;

    /// <summary>
    /// The resref this instance's own <c>MoviePlayHook</c> most recently captured from a
    /// mid-module <c>PlayMovie</c> call (no transition involved), with the serial the plugin
    /// stamped it with — already excluded, on the plugin side, against a name the engine's own
    /// transition queue already carries. False when nothing has been captured yet.
    ///
    /// The DETECTION half docs/research/trigger-relay.md §6 shipped without a hook for; see
    /// docs/research/cinematics.md's 2026-08-22 section. De-duplicate on the serial, the same
    /// convention every other plugin -&gt; agent announcement in this file uses.
    /// </summary>
    bool TryReadMidModuleMovie(out uint serial, out string resRef)
    {
        serial = 0;
        resRef = string.Empty;
        return false;
    }

    /// <summary>
    /// Asks the plugin to act on a PEER's mid-module movie: play it right now, or defer it
    /// onto this instance's own next transition, per <c>MovieRelayQueue.Decide</c> — the
    /// plugin makes the final call using engine state only it can read (idle / in dialogue /
    /// in combat / the transition queue). The caller is expected to have already filtered to
    /// the local module and a name that survived parsing; the plugin does not re-check either.
    /// Default false when this bridge has no plugin to ask.
    /// </summary>
    bool TryRequestMidModulePlay(string resRef) => false;

    /// <summary>
    /// What the plugin decided about the last <see cref="TryRequestMidModulePlay"/> request:
    /// <paramref name="applied"/> echoes the request's own serial once handled, and
    /// <paramref name="status"/> is the plugin's <c>MidModulePlayStatusCode</c> verdict. False
    /// when this bridge has no plugin to ask.
    /// </summary>
    bool TryReadMidModulePlayStatus(out uint applied, out uint status)
    {
        applied = 0;
        status = 0;
        return false;
    }

    /// <summary>False when this bridge cannot see or drive enemy health.</summary>
    bool SupportsEnemySync => false;

    /// <summary>
    /// True while the local player is in a conversation. Enemy state must not be published
    /// during one: the dialogue system disarms every hostile in this instance, and a peer
    /// mirroring that would see its own fight go quiet.
    /// </summary>
    bool IsInDialogue => false;

    /// <summary>
    /// True while the local player is inside the engine's own minigame mode (a swoop race
    /// or a forced turret sequence — <c>CSWCModule::GetMiniGame</c>, see
    /// docs/research/minigames.md). They are not meaningfully in the shared world for the
    /// duration: enemy/party presence this instance would publish is not the real fight,
    /// and damage forwarded from a peer should not land on someone who cannot be hit.
    /// </summary>
    bool IsInMinigame => false;

    /// <summary>
    /// Reads what the local player is currently hearing in a conversation, for the
    /// dialogue-spectate payload. Empty when not in a conversation, or when the plugin could
    /// not resolve the live line. Default is "no dialogue read" for bridges without a plugin.
    /// </summary>
    bool TryReadLocalDialogue(out string speaker, out string line)
    {
        speaker = string.Empty;
        line = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads what a spectating peer needs to run the SAME conversation locally: the tag of
    /// the NPC the local player is talking to, WHERE that conversation currently is (the
    /// dialogue entry index — this is what the follower is driven by), and the reply they
    /// last picked (diagnostic). Default is "nothing to follow" for bridges without a plugin,
    /// with the node reported as unknown rather than as node 0.
    /// </summary>
    bool TryReadLocalDialogueFollow(
        out string partnerTag, out uint replySerial, out int replyIndex, out byte[] pickLog,
        out int nodeIndex, out uint nodeSerial)
    {
        partnerTag = string.Empty;
        replySerial = 0;
        replyIndex = 0;
        pickLog = [];
        nodeIndex = -1;
        nodeSerial = 0;
        return false;
    }

    /// <summary>
    /// Tells the game which conversation a peer owns and which node it is on, so this
    /// instance runs the same one and is driven to the same line without being able to
    /// choose. Default false.
    /// </summary>
    bool WriteRemoteDialogue(
        bool active, string partnerTag, uint replySerial, int replyIndex, byte[]? pickLog,
        int nodeIndex, uint nodeSerial) => false;

    /// <summary>The object the local player is interacting with, so the session can claim it
    /// on their behalf. Default is "nothing claimed".</summary>
    bool TryReadLocalInteraction(out string tag, out uint kind, out bool active)
    {
        tag = string.Empty;
        kind = 0;
        active = false;
        return false;
    }

    /// <summary>Hands the game every object currently held by anyone, so it can hold the ones
    /// it owns still. Default false.</summary>
    bool WriteInteractionLocks(IReadOnlyList<InteractionLock> locks) => false;

    /// <summary>
    /// This instance's companion roster, straight off its own party table: which of the nine
    /// NPC slots are recruited, and which are in the active party. Default is "cannot read
    /// one", which is NOT the same as an empty roster and must never be published as one.
    /// </summary>
    bool TryReadLocalPartyRoster(out PartyRoster roster)
    {
        roster = PartyRoster.Empty;
        return false;
    }

    /// <summary>
    /// Hands the game the HOST's roster to reconcile against, or clears it.
    ///
    /// <paramref name="valid"/> false means "no authority is speaking here" — the host itself,
    /// a client with no level yet, a disabled channel — and the game must then leave its own
    /// roster completely alone. Default false.
    /// </summary>
    bool WriteHostPartyRoster(bool valid, PartyRoster roster) => false;

    /// <summary>Posts one line to the always-on chat overlay (used to surface a peer's
    /// dialogue). Default false when this bridge has no overlay.</summary>
    bool PostChatLine(string line) => false;

    /// <summary>
    /// Reads the module's creatures for the enemy channel. Used by whichever peer owns them.
    ///
    /// <paramref name="otherPlayers"/> is where the OTHER players are standing. The census is
    /// capped, and what the cap sheds is decided by distance — so without this the publisher
    /// only ever measures from itself, and the creatures around a partner across the module
    /// are the first thing dropped. See <see cref="KotorGameBridge.ReadEnemies"/>.
    /// </summary>
    List<EnemyState> ReadEnemies(IReadOnlyList<Vector3f> otherPlayers) => [];

    /// <summary>
    /// Every creature this engine currently knows about in the loaded area — not just the
    /// 32-slot combat census <see cref="ReadEnemies"/> caps to. Reuses the SAME confirmed
    /// enumeration <see cref="ReadEnemies"/> builds from (the ghost gate documented in
    /// docs/research/enemy-presence-sync.md), so an id published here is exactly as
    /// trustworthy as one published in the ordinary census. Excludes the player, party
    /// members (owned by other channels already) and any co-op phantom/materialised-stand-in
    /// body — never anyone's NPC to report on. Used by whichever peer owns enemies, for the
    /// liveness-reconciliation channel — see docs/research/creature-liveness.md.
    /// </summary>
    List<CreatureLivenessEntry> ReadCreatureLiveness() => [];

    /// <summary>
    /// Every item any of <paramref name="possessorIds"/> is carrying, by possessor plus resref
    /// plus full per-instance identity — the same sweep the <c>items</c>/<c>setitem</c> staging
    /// cheats use (<see cref="KotorGameBridge.ListItems"/>). Used at the moment one or more
    /// owner-authoritative enemies die to capture their real corpses' actual loot for
    /// <see cref="ICoopSession.PublishMaterialisedLoot"/> — see
    /// docs/research/enemy-materialisation.md §6.
    ///
    /// ⚠ TAKES A SET, NOT ONE ID. <see cref="KotorGameBridge.ListItems"/>'s own doc warns it is
    /// a full world memory sweep, "not free" — "call it from a command surface, never from the
    /// pump." A death-detection loop that fires per-creature would run one full sweep PER DEAD
    /// ENEMY on the same tick; an ambush wave dying together would stall the pump repeatedly.
    /// One sweep, filtered to however many died this tick, costs the same as one.
    /// </summary>
    IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPossessedItems(
        IReadOnlySet<uint> possessorIds) => [];

    /// <summary>Barks the plugin captured since the last look. See <see cref="BarkRecord"/>.</summary>
    List<BarkRecord> ReadBarks() => [];

    /// <summary>Hands a peer's bark to the plugin to replay on its copy of the speaker.</summary>
    bool TryWriteBark(
        uint speaker, ReadOnlySpan<byte> text, ReadOnlySpan<byte> soundResRef, uint flags, uint serial) => false;

    /// <summary>
    /// Turns off the local module's encounters. Called on every peer that does NOT own
    /// enemies: encounter spawns are the one engine-side source of object-id drift, and
    /// identical ids across instances are the key combat replication rests on. Returns
    /// how many encounters were disabled (0 when already done for this module).
    /// </summary>
    int DisableLocalEncounters() => 0;

    /// <summary>
    /// Turns the plugin's encounter watch on or off. On only for a peer that does NOT own
    /// enemies: the owner's engine fires its own encounters natively and has nothing to ask
    /// anyone for, so the watch would be pure cost there.
    /// </summary>
    void SetEncounterWatch(bool enabled) { }

    /// <summary>
    /// Non-owner side: the object ids of the encounters the local player is standing inside
    /// right now, as the plugin's watch reports them (the engine's own polygon test). Empty
    /// when the plugin is absent or the player is in open ground.
    /// </summary>
    IReadOnlyList<uint> ReadEnteredEncounters() => [];

    /// <summary>
    /// Owner side: asks the plugin to run the engine's own "should this encounter fire"
    /// test for each (encounter, peer) pair, standing the peer's phantom in for the player
    /// who actually walked in. Repeats are harmless — the engine refuses an encounter that
    /// has already started. Returns false when there is no plugin.
    /// </summary>
    bool TryRequestEncounterFire(IReadOnlyList<(uint EncounterId, uint PeerId)> requests) => false;

    /// <summary>
    /// Non-owner side of TriggerRelay, mirroring <see cref="SetEncounterWatch"/>: run the
    /// generic-trigger watch only on a peer that is not enemy-authority. The owner's own
    /// engine already fires generic triggers for its own real character correctly and has
    /// nothing to ask anyone for.
    /// </summary>
    void SetTriggerWatch(bool enabled) { }

    /// <summary>
    /// Non-owner side: the object ids of the GENERIC triggers the local player is standing
    /// inside right now AND whose OnEnter script matched the coop.ini allowlist pattern, as
    /// the plugin's watch reports them. Empty when the plugin is absent, the player is
    /// outside every relay-eligible polygon, or the channel is disabled.
    /// </summary>
    IReadOnlyList<uint> ReadEnteredTriggers() => [];

    /// <summary>
    /// Every peer, protocol 36: the object ids of GENERIC triggers this instance's OWN engine
    /// has natively fired for its own real character since last read — the plugin's
    /// <c>TriggerFireHook</c> observer, already allowlist- and eligibility-filtered the same
    /// way <see cref="ReadEnteredTriggers"/>'s watch is. Unlike that watch (a currently-standing
    /// SET, level-published every frame), the plugin controller only adds an id here once it
    /// has actually been observed firing — see docs/research/trigger-relay.md's protocol-36
    /// section. Empty when the plugin is absent, nothing has fired natively yet, or the
    /// channel is disabled.
    /// </summary>
    IReadOnlyList<uint> ReadNativeFiredTriggers() => [];

    /// <summary>
    /// Owner side: asks the plugin to run the SAME OnEnter script the requesting peer's own
    /// engine already ran, against that peer's phantom standing in as the entering object.
    /// Unlike <see cref="TryRequestEncounterFire"/>, a repeat is NOT free here — a generic
    /// trigger carries no engine one-shot flag — so callers are expected to only ever pass
    /// requests <c>KotorCoop.Session.TriggerRelayStore.Collect</c> has not already marked
    /// fired. Returns false when there is no plugin.
    /// </summary>
    bool TryRequestTriggerFire(IReadOnlyList<(uint TriggerId, uint PeerId)> requests) => false;

    /// <summary>
    /// Applies the owning peer's enemy state to this instance: mirror their health, and
    /// stop local fighting from killing anything the owner still considers alive.
    /// </summary>
    bool TryWriteEnemies(IReadOnlyList<EnemyState> enemies) => false;

    /// <summary>ROUND 39: the owner id of the mirrored enemy this instance is currently
    /// letting fight the local player for real under `local_combat`, or 0 for none. The
    /// caller must keep this id out of what it writes back via <see cref="TryWriteEnemies"/> —
    /// see <c>KotorGameBridge.ReadLocalCombatExcludedId</c> for why.</summary>
    uint ReadLocalCombatExcludedId() => 0;

    /// <summary>
    /// Non-owner side: hands the host's latest creature-liveness view to the plugin's
    /// reconciliation controller, so a body left alive here that the host says is dead or
    /// destroyed can be corrected through the ordinary kill/destroy paths. Capacity-limited
    /// (<c>SharedBlock.LivenessCapacity</c>); the caller should order the entries that need
    /// ACTING ON (dead/gone) first, since those are what a truncation must never drop. See
    /// docs/research/creature-liveness.md.
    /// </summary>
    bool TryWriteCreatureLiveness(IReadOnlyList<CreatureLivenessEntry> entries) => false;

    /// <summary>
    /// Non-owner side: how many "host says alive, but this instance's own engine already had
    /// it dead or gone" anomalies the plugin's reconciliation controller has noticed since it
    /// started — a cumulative counter, never reset by a module change. Never acted on beyond
    /// logging/counting (the right fix for a genuine anomaly is upstream, not a forced
    /// resurrection) — see docs/research/creature-liveness.md.
    /// </summary>
    uint ReadLivenessAnomalyCount() => 0;

    /// <summary>False when this bridge cannot see or drive party companions.</summary>
    bool SupportsPartySync => false;

    /// <summary>
    /// Host side: reads the active party companions (keyed by NPC slot), whose movement
    /// the host's own engine AI drives. The client mirrors these.
    /// </summary>
    List<PartyMemberState> ReadPartyMembers() => [];

    /// <summary>
    /// The portrait resrefs of this instance's party, PC first — what a host advertises so a
    /// browsing player can see who they would be joining. Empty when unknown, which the
    /// browser draws as no faces rather than as an error.
    /// </summary>
    IReadOnlyList<string> ReadPartyPortraits() => [];

    /// <summary>
    /// Client side: freezes the local party AI (solo mode) so companions do not follow or
    /// act on their own, then mirrors the host's companions onto the matching local
    /// creatures by slot. Returns how many were placed. The local PC — even if it is a
    /// companion the player took control of after a death — is never mirrored.
    /// </summary>
    int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members) => 0;

    /// <summary>Client side: releases the solo-mode freeze this bridge asserted, so a
    /// single-player continuation after the session is normal. Called on shutdown.</summary>
    void ReleasePartyFreeze() { }

    /// <summary>Client side: pins player input control to the local PC so a client cannot take
    /// over the host-owned companions. Object-id write, crash-proof. False if not resolvable.</summary>
    bool LockControlToPlayer() => false;

    /// <summary>False when this bridge cannot mediate deaths (no plugin).</summary>
    bool SupportsDeathHandling => false;

    /// <summary>
    /// Holds back the two engine reflexes a co-op session must arbitrate itself: the
    /// game-over screen when this instance's whole party is down, and control auto-swapping
    /// off the player's own PC. Clearing <paramref name="suppressGameOver"/> releases a
    /// genuine, session-wide party wipe.
    /// </summary>
    bool SetDeathSuppression(bool suppressGameOver, bool suppressSwap) => false;

    /// <summary>True (once) when the engine tried to end the game and the plugin swallowed
    /// it — i.e. every party member on THIS instance is down.</summary>
    bool TryTakeGameOverAttempt() => false;

    /// <summary>Stands the local PC back up (a peer reached them). 0 = plugin default HP.</summary>
    bool RequestRevive(int hitPoints = 0) => false;

    /// <summary>False when this bridge cannot see or drive party gold / PC experience.</summary>
    bool SupportsProgressionSync => false;

    /// <summary>Host side: reads the party's gold and the PC's experience.</summary>
    bool TryReadProgression(out uint gold, out uint experience)
    {
        gold = 0;
        experience = 0;
        return false;
    }

    /// <summary>Client side: overwrites local gold and PC experience with the host's absolute
    /// values, so both parties progress together.</summary>
    bool TryWriteProgression(uint gold, uint experience) => false;

    /// <summary>
    /// Mirroring side: how much damage this instance has DEALT to each enemy since its
    /// object ids were last assigned, plus the ledger generation those totals belong to.
    /// Cumulative and monotonic within a generation; empty when no plugin is attached.
    /// </summary>
    (uint Generation, List<EnemyDamageEntry> Damage) ReadDamageDealt() => (0, []);

    /// <summary>
    /// Enemy-authority side only: hands the plugin the total every peer has dealt to each
    /// of our enemies, keyed to <paramref name="epoch"/> (the module the ids belong to).
    /// The plugin applies the increase as real engine damage on the game thread — which is
    /// also how a peer's kill happens, because an enemy whose accumulated damage exceeds
    /// its health simply dies, with the corpse, loot and OnDeath the engine runs for any
    /// other death.
    /// </summary>
    bool TryPublishDamageOwed(uint epoch, IReadOnlyList<EnemyDamageEntry> owed) => false;

    /// <summary>False when this bridge has no plugin to run staging cheats through.</summary>
    bool SupportsCheats => false;

    /// <summary>
    /// Publishes one staging cheat for the plugin to run on the game thread, returning the
    /// serial it was filed under so the answer can be matched to it.
    ///
    /// Fire-and-forget by design: every one of these ends in an engine call, engine calls
    /// only happen on the game's own thread, and the agent is not on it. The answer comes
    /// back through <see cref="TryReadCheatAnswer"/> a frame or two later.
    /// </summary>
    bool TryRequestCheat(CheatRequest request, out uint serial)
    {
        serial = 0;
        return false;
    }

    /// <summary>
    /// Reads the plugin's answer to the last cheat: which serial it finished, the status,
    /// and the sentence describing what it actually saw. False when there is no plugin.
    /// </summary>
    bool TryReadCheatAnswer(out uint applied, out int status, out string detail)
    {
        applied = 0;
        status = 0;
        detail = string.Empty;
        return false;
    }

    /// <summary>False when this bridge cannot see or drive the quest journal.</summary>
    bool SupportsJournalSync => false;

    /// <summary>Journal entries that changed locally since the last call — added, raised,
    /// lowered, or removed. See <see cref="JournalChange"/>.</summary>
    List<JournalChange> CollectChangedJournal() => [];

    /// <summary>
    /// Sets a journal entry to a peer's state through the game's own path. Direction-neutral
    /// since protocol 31 — the session is the authority, including on a legitimate decrease.
    /// Must be a no-op (returning false) when the game already holds exactly that state.
    /// </summary>
    bool TryApplyJournalEntry(string questTag, int state) => false;

    /// <summary>
    /// Takes a journal entry out of the game entirely — the removal twin of
    /// <see cref="TryApplyJournalEntry"/>, for a peer's `RemoveJournalQuestEntry`. Must be a
    /// no-op (returning false) when the game does not currently hold the tag. See
    /// docs/research/quest-sync.md for why the concrete implementation is gated behind an
    /// opt-in flag: the native call it uses was found by static disassembly, not verified
    /// live.
    /// </summary>
    bool TryRemoveJournalEntry(string questTag) => false;
}
