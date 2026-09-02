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
    /// ⚠ 26 is the highest number taken (25/26: PartyPoolAdd/Remove, below). Take the next
    /// FREE one for a new verb —
    /// CheatKindSyncTests.NoTwoCheatKindsShareANumber will fail the build on a collision.
    /// </summary>
    public const uint WalkTo = 24;

    /// <summary>
    /// Adds <c>Amount</c> units of a resref+identity stack to the shared party pool
    /// (<c>CSWPartyTable+0x118</c>): spawns the item straight into the local player's own
    /// inventory (the same <c>CreateItemOnObject</c> VM routine <see cref="GiveLoot"/> already
    /// uses — there is no VM path that targets the pool directly, since it is not a scriptable
    /// object), applies its identity exactly like <see cref="SetItem"/>, then unlinks it from
    /// the player's own backpack and links it into the pool's repository through the engine's
    /// own <c>CItemRepository::InsertItem</c>. <c>Text</c> is the resref, <c>Amount</c> the
    /// stack size, Charges/MaxCharges/Upgrades/ItemFlags the rest of the stack identity — the
    /// same five fields <see cref="GiveLoot"/> and <see cref="SetItem"/> already carry. See
    /// SharedBlock.CheatKindPartyPoolAdd and docs/research/party-pool-sync.md.
    /// </summary>
    public const uint PartyPoolAdd = 25;

    /// <summary>
    /// Removes <c>Amount</c> units of a resref+identity stack from the shared party pool,
    /// through the engine's own <c>CItemRepository::RemoveItem</c> — greedily over whichever
    /// live items in the pool match the requested resref (by Tag, case-insensitive) and
    /// identity, reducing or deleting entries until the requested count is accounted for or the
    /// pool runs out of matches. Same field layout as <see cref="PartyPoolAdd"/>. See
    /// SharedBlock.CheatKindPartyPoolRemove and docs/research/party-pool-sync.md.
    /// </summary>
    public const uint PartyPoolRemove = 26;

    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/disconnect-reconnect.md §9): tears the
    /// world down and shows the main menu, via the engine's own
    /// <c>CClientExoAppInternal::ShutDownToMainMenu</c> — the same function a real "Quit Game
    /// -&gt; Yes" click reaches. Built to exercise the Away flag's third shape (no player object
    /// at all) without navigating the vanilla pause menu by mouse, which a prior round found
    /// does not land clicks on a demo.ps1-repositioned window and once wedged an instance via
    /// blind keyboard navigation (docs/research/session-save.md). See
    /// <c>SharedBlock.CheatKindQuitToMenu</c>. Takes no arguments; local only.
    ///
    /// ⚠ 27 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint QuitToMenu = 27;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/dialogue-consequences.md's script-item-sync
    /// round): gives an item straight into the local player's inventory with NO suppression-ledger
    /// entry recorded, simulating an untracked script grant (OnEnter/OnDeath/heartbeat) for
    /// testing <c>script_item_sync</c>. See <c>SharedBlock.CheatKindScriptGive</c>. <c>Address</c>
    /// is the resref, <c>Amount</c> the stack size. Local only.
    ///
    /// ⚠ 28 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint ScriptGive = 28;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/dialogue-consequences.md's script-item-sync
    /// round, Controls 2/3 — "the sub-case most likely to fail live" and the one the prior round
    /// could not reach): reproduces a genuine container/corpse loot take through TWO real engine
    /// calls instead of the GUI click neither this codebase nor its test rig can deliver
    /// reliably — removes the target container's FIRST item (the same real
    /// <c>CSWSPlaceable::RemoveItem</c> primitive <c>TryWriteContainerLoot</c>'s apply path
    /// already uses) and gives an equivalent copy straight to the local player, so both the real
    /// <c>CollectContainerLootRemovals</c> removal signal AND the real inventory-row-appeared
    /// signal fire, exactly as they would from a real take — unlike <c>scriptgive</c>, which
    /// fabricates only the second half with no container involved at all. See
    /// <c>SharedBlock.CheatKindLootTake</c>. <c>Address</c>/<c>Target</c> name the container
    /// (tag or object id; blank = nearest), same shape as <c>UsePlaceable</c>. Local only.
    ///
    /// </summary>
    public const uint LootTake = 29;

    /// <summary>
    /// Inserts <c>Amount</c> units of a resref+identity stack into store <c>Target</c>'s stock
    /// (<c>CSWSStore+0x230</c>): spawns the item into the local player's own inventory (the
    /// SAME <c>CreateItemOnObject</c> VM routine <see cref="PartyPoolAdd"/> already uses),
    /// applies its identity exactly like <see cref="SetItem"/>, then unlinks it from the
    /// player's own backpack and links it into the RESOLVED store's repository through
    /// <c>CItemRepository::InsertItem</c> — the store is resolved from <c>Target</c> (its
    /// object id) via the engine's own type-checked
    /// <c>CServerExoAppInternal::GetStoreByGameObjectID</c>, needing no GUI open. <c>Text</c> is
    /// the resref, <c>Amount</c> the stack size, Charges/MaxCharges/Upgrades/ItemFlags the rest
    /// of the stack identity. See <c>SharedBlock.CheatKindStoreInsert</c> and
    /// docs/research/store-sync.md.
    /// </summary>
    public const uint StoreInsert = 30;

    /// <summary>
    /// Removes <c>Amount</c> units of a resref+identity stack from store <c>Target</c>'s stock,
    /// through the engine's own <c>CItemRepository::RemoveItem</c> — greedily over whichever
    /// live items in that store's repository match the requested resref (by Tag) and identity,
    /// reducing or deleting entries until the requested count is accounted for. An item whose
    /// Infinite bit is set (item flags <c>+0x288</c> bit 2) is skipped during matching — see
    /// docs/research/store-sync.md §1.5/§2.4. Same field layout as <see cref="StoreInsert"/>.
    /// See <c>SharedBlock.CheatKindStoreRemove</c>.
    ///
    /// (see CheatKindSyncTests.NoTwoCheatKindsShareANumber)
    /// </summary>
    public const uint StoreRemove = 31;

    /// LIVE-TEST PRIMITIVE (docs/research/companion-state-sync.md's live test plan §5): adds
    /// <c>Amount</c> (signed) to <c>CSWSCreatureStats::experience</c> on the creature named by
    /// <c>Target</c> (0 = local player), clamped at 0 — simulates a script-granted XP award
    /// aimed at one creature only, testing <c>companion_xp_sync</c>'s merge. See
    /// SharedBlock.CheatKindGrantXp.
    /// </summary>
    public const uint GrantXp = 32;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/companion-state-sync.md's live test plan §5):
    /// re-equips the creature named by <c>Target</c> (0 = local player) with <c>Text</c> (a
    /// resref) into inventory slot <c>Amount</c> (1 = body, 4 = right weapon, 5 = left weapon;
    /// 0/default = right weapon), through <c>Odyssey.EquipItemFromTemplate</c>. See
    /// SharedBlock.CheatKindEquip.
    ///
    /// ⚠ 33 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint Equip = 33;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/quest-sync.md's 2026-08-27 addendum): writes script
    /// global <c>Text</c> (the name, max 22 chars) to <c>Amount</c> (a signed value) through the
    /// engine's own type-checked setter — <c>ItemFlags</c> 1 selects the boolean setter, 0 (or
    /// unset) the number setter (reused fields, no new ones — this cheat never touches an item).
    /// See SharedBlock.CheatKindSetGlobal.
    /// </summary>
    public const uint SetGlobal = 34;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/minigames.md's 2026-08-27 addendum): starts a Pazaak
    /// hand against opponent <c>Target</c> (object id) or <c>Text</c> (tag) — both empty means
    /// the engine's own default opponent — with pazaak-deck index <c>Amount</c> (default 0),
    /// through <c>CClientExoApp::StartPazaakGame</c>, no wager, no tutorial. See
    /// SharedBlock.CheatKindPazaak.
    /// </summary>
    public const uint Pazaak = 35;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/store-sync.md's 2026-08-27 addendum): opens the store
    /// GUI against store <c>Target</c> (object id) or <c>Text</c> (tag) through
    /// <c>CGuiInGame::ShowStoreGui</c> called directly, then writes the resolved store's object
    /// id into the new panel and forces a repopulate. See SharedBlock.CheatKindOpenStore.
    /// </summary>
    public const uint OpenStore = 36;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/galaxy-map-and-party-select.md's 2026-08-28
    /// addendum): confirms the native party-selection screen (<c>CSWGuiPartySelection</c>,
    /// reached from <c>CGuiInGame+0x078</c>) with its CURRENT on-screen selection, through
    /// <c>CSWGuiPartySelection::ConfirmPartySelection</c> (0x006BEC90) — the same handler a
    /// real Done/Accept click reaches, which itself gates on the shared
    /// <c>CSWGuiMessageBox+0x64</c> bit-0 precondition (session-save.md's own
    /// <c>msgboxpoke</c> trap) before calling <c>AcceptParty</c> (0x006BE560). Pokes that same
    /// bit first, the same established workaround `loadgame` needs on a fresh all-console
    /// session. Takes no arguments; local only — see SharedBlock.CheatKindPartySelectOk.
    ///
    /// ⚠ 37 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint PartySelectOk = 37;

    /// <summary>
    /// LIVE-TEST/DIAGNOSTIC PRIMITIVE (docs/research/combat-participation.md §10): reads the
    /// engine's own <c>GetAIStateReputation</c> verdict (and the raw <c>faction_id</c> dword at
    /// <c>stats+0x78</c>) for creature <c>Target</c> (0 = local player) versus <c>Amount</c>
    /// (0 = local player), both directions. Read-only, no engine write. See
    /// SharedBlock.CheatKindFaction.
    ///
    /// ⚠ 38 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint Faction = 38;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/minigames.md's live-pass entry): ends the current
    /// Pazaak hand through <c>CClientExoApp::EndPazaakGame</c> (0x005EDDD0), so
    /// <c>FlagInMinigame</c> can be watched clearing without a real wagered hand played to
    /// completion through the GUI. Takes no arguments; local only. See
    /// SharedBlock.CheatKindPazaakEnd.
    ///
    /// ⚠ 39 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint PazaakEnd = 39;

    /// <summary>
    /// DEV VERB (docs/research/store-sync.md's 2026-08-28 addendum): buys a resref from a
    /// store through the REAL <c>CSWGuiStore::BuyItem</c> click handler, so gold and item both
    /// move through the engine's own path. Requires the target store's own GUI already open.
    /// <c>Target</c> nonzero = store object id (<c>Text</c> then the bare resref); <c>Target</c>
    /// zero means the store was a TAG (<c>Text</c> packs <c>"tag|resref"</c>). <c>Amount</c> is
    /// the count. See <c>SharedBlock.CheatKindStoreBuy</c>.
    ///
    /// ⚠ 40 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint StoreBuy = 40;

    /// <summary>
    /// DEV VERB (docs/research/store-sync.md's 2026-08-28 addendum): sells a resref from the
    /// local player's own inventory to a store through the REAL <c>CSWGuiStore::SellItem</c>
    /// click handler. Same field layout as <see cref="StoreBuy"/>. See
    /// <c>SharedBlock.CheatKindStoreSell</c>.
    ///
    /// ⚠ 41 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint StoreSell = 41;

    /// <summary>
    /// DEV VERB (docs/research/dialogue-sync.md's 2026-08-28 addendum): starts a conversation
    /// between the local player and an NPC through the engine's own
    /// <c>ActionStartConversation</c> VM routine (id 204), dispatched through the real
    /// action-queue so the existing dialogue hooks observe it like a real click. <c>Target</c>
    /// nonzero = the NPC's object id (<c>Text</c> then the optional dialog resref override, or
    /// empty for the NPC's own default); <c>Target</c> zero means the NPC was a TAG (<c>Text</c>
    /// packs <c>"tag"</c> or <c>"tag|dlg-resref"</c>). See <c>SharedBlock.CheatKindStartDialog</c>.
    ///
    /// ⚠ 42 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint StartDialog = 42;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (docs/research/star-forge-finale.md §11): calls the engine's
    /// own <c>RemovePartyMember(int nNPC)</c> VM routine, so <c>RemovePartyMemberHook</c>'s
    /// detour can be exercised without reaching the Bastila dialogue's own entry 70. <c>Amount</c>
    /// is the NPC roster slot (0-8). See <c>SharedBlock.CheatKindRemoveParty</c>.
    ///
    /// ⚠ 43 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint RemoveParty = 43;

    /// <summary>
    /// DEV VERB (docs/research/store-sync.md's live pass): sets the party's gold directly to
    /// <c>Amount</c> through <c>CSWSCreature::SetGold 0x4EDD90</c>, clamped server-side to
    /// 999,999,999. Repairs a save whose gold was driven negative-then-wrapped by an
    /// unaffordable <c>buy</c> before <c>Odyssey.BuyFromStore</c>'s own affordability guard
    /// existed — see docs/live-verification.md. See <c>SharedBlock.CheatKindSetGold</c>.
    ///
    /// ⚠ 44 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint SetGold = 44;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (docs/research/party-reconciliation.md Round 96): dumps a
    /// companion's follow-record fields then forces <c>record+4 = 10</c> (<c>TeleportForward</c>)
    /// unconditionally, regardless of current phase — unlike the automatic
    /// <c>party_follow_phase2_kick</c> pass, which only fires from phase 2. <c>Amount</c> is the
    /// party-roster slot (0-8). See <c>SharedBlock.CheatKindFollowKick</c>.
    ///
    /// ⚠ 45 is the highest number taken now. Take the next FREE one for a new verb.
    /// </summary>
    public const uint FollowKick = 45;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (docs/research/party-reconciliation.md Round 105): dumps a
    /// companion's follow-record wedge signature, then applies one of <c>FollowStallRule</c>'s
    /// four candidate recoveries unconditionally — no wedge check, no stall timer, no cooldown,
    /// unlike the automatic <c>party_follow_stall_recovery</c> pass. <c>Amount</c> is the
    /// party-roster slot (0-8); <c>Target</c> is the method (1 phasereset, 2 followleader,
    /// 3 positionparty, 4 priortie; 0 = whatever coop.ini selected). See
    /// <c>SharedBlock.CheatKindFollowStall</c>.
    ///
    /// ⚠ 56 is taken from a reserved per-stream range, not the running maximum — check
    /// <c>SharedBlock</c>'s own numbering note before adding another.
    /// </summary>
    public const uint FollowStall = 56;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/dialogue-skip-desync.md): presses skip on the dialogue
    /// panel through the ENGINE'S OWN INPUT DISPATCH
    /// (<c>CSWGuiDialog::HandleInputEvent(panel, 0x27, 1)</c>), which is the one route that
    /// actually passes through <c>DialogueSkipHook</c> — the spectator gag. <see cref="SkipLine"/>
    /// does not: it pokes the owner's object flag directly and refuses on a spectator, which is
    /// precisely the instance under test. Takes no arguments. See
    /// <c>SharedBlock.CheatKindSkipKey</c>.
    /// </summary>
    public const uint SkipKey = 59;

    /// <summary>
    /// Saves the game through the engine's own quicksave path
    /// (<c>CClientExoAppInternal::DoQuickSave</c>). Takes no arguments. Used by
    /// <c>join_mode = newcharacter</c> to obtain a first-time joiner's freshly created
    /// character in the only portable form KOTOR has for one — a save — and available as a
    /// console verb in its own right. See <c>SharedBlock.CheatKindQuickSave</c>.
    /// </summary>
    public const uint QuickSave = 60;

    /// LIVE-TEST PRIMITIVE (docs/research/shared-inventory-v2.md §14): closes the full-screen
    /// in-game GUI (equipment / inventory / character / journal / map / options) through the
    /// engine's own <c>HideSWInGameGui</c>. No arguments; local only.
    ///
    /// It is the counterpart to <see cref="OpenStore"/>'s own refusal: that screen sets
    /// <c>CGuiInGame+0x30</c>, which <c>ShowStoreGui</c> gates on, and nothing else in the
    /// console can clear it — which is why the client half of four consecutive live store rounds
    /// never ran. See <c>SharedBlock.CheatKindCloseGui</c>.
    ///
    /// ⚠ 53 is taken from a reserved per-stream range, not the running maximum — check
    /// <c>SharedBlock</c>'s own numbering note before adding another.
    /// </summary>
    public const uint CloseGui = 53;

    /// <summary>Pops the GUI manager's own modal stack, which is what actually lets
    /// <see cref="CloseGui"/> succeed — the engine refuses its own full-screen teardown while
    /// that stack is occupied. See <c>SharedBlock.CheatKindPopModal</c>.</summary>
    public const uint PopModal = 54;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (docs/research/death-handling.md's 2026-09-02 follow-up):
    /// real damage through the engine's own <c>EffectDamage</c>/<c>DoDamage</c> path — shields,
    /// difficulty scaling and both 1-hp floors apply exactly as they would to a live hit.
    /// <c>Amount</c> is the damage; <c>Target</c> is <see cref="LocalPlayerTarget"/> for
    /// yourself or a party-roster slot 0-8. See <c>SharedBlock.CheatKindHurt</c>.
    ///
    /// ⚠ 63, next free after every noted range through 62 as of 2026-09-02.
    /// </summary>
    public const uint Hurt = 63;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (docs/research/death-handling.md's 2026-09-02 follow-up):
    /// forces the SAME genuine temp-death state a real lethal hit produces (bypassing any
    /// <c>min1hp</c> pin, then restoring it) on yourself or a party-roster companion — the
    /// live-testable trigger for the whole downed/revive/game-over pipeline. <c>Target</c> is
    /// <see cref="LocalPlayerTarget"/> or a party-roster slot 0-8, same as <see cref="Hurt"/>.
    /// See <c>SharedBlock.CheatKindDown</c>.
    ///
    /// ⚠ 64, next free after every noted range through 62 as of 2026-09-02.
    /// </summary>
    public const uint Down = 64;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (Tier 2 item 18 task 2's own "Live test plan" follow-up,
    /// docs/research/galaxy-map-and-party-select.md's 2026-09-03 addendum): opens this
    /// instance's own galaxy-map GUI through <c>CGuiInGame::ShowGalaxyMapGui</c>, called
    /// directly — so the confirm-refusal splice (<c>galaxy_map_host_only</c>) can finally be
    /// exercised live without a human clicking. Takes no arguments. See
    /// <c>SharedBlock.CheatKindGalaxyMapOpen</c>.
    ///
    /// ⚠ 65, next free after every noted range through 64 as of 2026-09-03.
    /// </summary>
    public const uint GalaxyMapOpen = 65;

    /// <summary>The counterpart to <see cref="GalaxyMapOpen"/>, through
    /// <c>CGuiInGame::HideGalaxyMapGui</c>. Takes no arguments. See
    /// <c>SharedBlock.CheatKindGalaxyMapClose</c>.
    ///
    /// ⚠ 66, next free after every noted range through 65 as of 2026-09-03.
    /// </summary>
    public const uint GalaxyMapClose = 66;

    /// <summary>Reports the galaxy-map confirm hook's own <c>RefusedCount</c>/<c>AllowedCount</c>
    /// counters and whether the splice installed — a pure read, no engine call. See
    /// <c>SharedBlock.CheatKindGalaxyMapStats</c>.
    ///
    /// ⚠ 67, next free after every noted range through 66 as of 2026-09-03.
    /// </summary>
    public const uint GalaxyMapStats = 67;

    /// <summary>
    /// <c>spawncreature &lt;template&gt; [count] [x y [z]]</c> (alias <c>spawn</c>) —
    /// DEV-ONLY TEST PRIMITIVE (docs/research/creature-liveness.md's 2026-09-03 checklist item
    /// 6): spawns <c>Amount</c> hostile creatures (default 1, max 32) from the .utc template
    /// resref in <c>Text</c> through the engine's own CreateObject path, next to the local
    /// player by default or at an explicit <c>X</c>/<c>Y</c>/<c>Z</c> — same origin-sentinel
    /// convention <see cref="Teleport"/>/<see cref="WalkTo"/> already use. See
    /// <c>SharedBlock.CheatKindSpawnCreature</c>. Local only, and only the local instance
    /// spawns: on a CLIENT the spawned creatures are local-only, not carried by peer sync.
    ///
    /// ⚠ 68, next free after every noted range through 67 as of 2026-09-03.
    /// </summary>
    public const uint SpawnCreature = 68;
    /// Posts the SAME door-crossing event (type <c>0x1E</c>) the engine's own movement update
    /// posts when a creature walks THROUGH a door's sub-area — built exactly the way
    /// <c>CSWSCreature::AIActionMoveToPoint</c> builds it (0x005200C5-0x005201A7), instead of
    /// queuing a walk and hoping the pathfinder actually reaches the threshold. <c>usedoor</c>'s
    /// own walk has stalled 1.8-5.9 m short of the door on every live attempt to date
    /// (docs/research/cinematics.md §8.3b), so the "a real crossing opens the transition arm"
    /// half of the warp-crash fix has never actually been exercised — this is the instrument
    /// that exercises it directly. <c>Text</c> is the door tag, <c>Target</c> its object id;
    /// both empty means "the nearest module transition door", the exact same resolution
    /// <see cref="UseDoor"/> uses (opening the door first if it is shut — a real crossing needs
    /// an open door exactly as much as a walk does). See SharedBlock.CheatKindCrossDoor and
    /// docs/research/cinematics.md's 2026-09-03 addendum.
    ///
    /// ⚠ 69, next free after every noted range through 68 (SpawnCreature) as of 2026-09-03 — the branch had claimed 65, which the galaxy-map verbs took first.
    /// </summary>
    public const uint CrossDoor = 69;

    /// <summary>
    /// <c>galaxyaccept</c> — LIVE-TEST PRIMITIVE (docs/research/galaxy-map-and-party-select.md's
    /// 2026-09-03 `galaxyaccept` addendum): presses Accept on the galaxy map through the
    /// engine's own input dispatch, the SAME shape <see cref="SkipKey"/> already uses for the
    /// dialogue panel — the only route that reaches the confirm-refusal splice
    /// (<c>galaxy_map_host_only</c>). Takes no arguments. See
    /// <c>SharedBlock.CheatKindGalaxyAccept</c>.
    ///
    /// ⚠ 70, next free after every noted range through 69 (CrossDoor) as of 2026-09-03.
    /// </summary>
    public const uint GalaxyAccept = 70;

    /// <summary>
    /// <c>galaxymap select &lt;n&gt;</c> — writes the galaxy map's selected-planet field
    /// directly (<c>Amount</c> carries the planet index, 0-15), independent of whether the map
    /// screen is open. See <c>SharedBlock.CheatKindGalaxyMapSelect</c>.
    ///
    /// ⚠ 71, next free after every noted range through 70 as of 2026-09-03.
    /// </summary>
    public const uint GalaxyMapSelect = 71;

    /// <summary>
    /// <c>galaxymap unlock &lt;n&gt;</c> — STAGING VERB: marks planet <c>Amount</c> (0-15)
    /// available and selectable in the party table so <see cref="GalaxyMapSelect"/> can succeed
    /// on a save that has not unlocked it. See <c>SharedBlock.CheatKindGalaxyMapUnlock</c>.
    ///
    /// ⚠ 72, next free after every noted range through 71 as of 2026-09-03.
    /// </summary>
    public const uint GalaxyMapUnlock = 72;

    /// <summary>
    /// Mirrors <c>SharedBlock.CheatTargetLocalPlayer</c> — equal by VALUE (not by reference;
    /// the plugin is NativeAOT win-x86 and cannot be referenced from here), chosen because it
    /// is also the engine's own verified player object id (docs/re/object-model.md), so it
    /// never needs translation on either side. Used by <see cref="Hurt"/>/<see cref="Down"/>'s
    /// "blank slot = yourself" default.
    /// </summary>
    public const uint LocalPlayerTarget = 0x7FFFFFFF;
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
    IPartyPoolBridge,
    IStoreBridge,
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
