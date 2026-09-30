using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Both halves of the per-object interaction lock.
///
/// PUBLISHES what the local player is interacting with, so the session can claim it; and
/// APPLIES every lock a PEER holds, by holding those creatures still here.
///
/// The defect it exists for: ownership in this mod is one-directional
/// (docs/research/shared-state-audit.md §4). The host owns companions and NPCs, so when a
/// client walks up to Trask and starts talking, the client is looking at a body it drives
/// from the host's stream while the HOST's own AI — never told anything happened — carries on
/// walking the real Trask around. Reported from play as "Trask is still moving around on the
/// host and other players' screens" during a client's conversation.
///
/// The claim is also what makes a contested NPC fair. It goes up the moment a player starts
/// interacting, which is EARLIER than a conversation exists, so it records who really got
/// there first — where the dialogue token cannot, because the host's own claim reaches the
/// arbiter in-process while a client's crosses the network.
///
/// Kill switch: <c>interaction_locks=0</c> in coop.ini.
/// </summary>
internal sealed unsafe partial class InteractionLockController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("interaction_locks", true);

    /// <summary>
    /// Pull this player into a conversation another player has started with an NPC, instead of
    /// waiting for them to click the same NPC themselves — the seamless version of what
    /// <c>force_dialogue_party</c> used to gate (docs/research/shared-state-audit.md's
    /// "force_dialogue_party, and why it is OFF").
    ///
    /// That flag shipped OFF for two reasons. First, it is the behaviour that was already
    /// removed once: an earlier build opened the peer's conversation unconditionally and
    /// players were yanked out of what they were doing from anywhere in the module — walking
    /// away while your partner talks to a shopkeeper is a legitimate thing to want, so joining
    /// became a click. <see cref="DialogueAutoJoinMode.Off"/> (the default here, since
    /// 2026-08-23 — docs/research/dialogue-sync.md's Item 1) is the user's own stated
    /// preference: an ORDINARY conversation stays a click, full stop, with the walk-over notice
    /// only shown under the opt-in <see cref="DialogueAutoJoinMode.Near"/>
    /// (<see cref="_farNoticeUntilMs"/>). A conversation the vanilla ENGINE ITSELF forces —
    /// an ambush, a scripted cutscene — auto-joins regardless of this setting; see
    /// <see cref="TryForceOpen"/>'s <c>isForced</c> path, which is not this flag's concern at
    /// all. Second, and now resolved: a recruitment/dismissal conversation's
    /// <c>AddPartyMember</c>/<c>RemovePartyMember</c> now HAS a channel
    /// (<c>PartyRosterController</c>, <c>dialogue_recruit_sync</c>) — the gap that made turning
    /// the old flag on unsafe for Trask's recruitment specifically no longer exists.
    /// </summary>
    private readonly DialogueAutoJoinMode _autoJoinMode =
        DialogueAutoJoinRule.ParseMode(PluginConfig.Read("dialogue_auto_join"));

    /// <summary><c>dialogue_auto_join_range</c> (default 20 game units) — only meaningful
    /// under <see cref="DialogueAutoJoinMode.Near"/>. <c>dialogue_join_teleport</c> (a separate,
    /// smaller, engine-verified 10-unit gate — docs/research/dialogue-join-latency.md) still
    /// handles the actual walk/snap once a join is decided; this is purely the "does being
    /// auto-joined feel reasonable from here" threshold.</summary>
    private readonly float _autoJoinRange = PluginConfig.ReadFloat("dialogue_auto_join_range", 20f);

    /// <summary>Pause between attempts to be pulled into a peer's conversation. The open is a
    /// server round trip that walks the player into range first, so retrying faster than this
    /// would queue several requests for one conversation.</summary>
    private const long ForceOpenRetryMs = 4000;

    private long _nextForceOpenMs;

    /// <summary>Frames between passes. Holding a body still is not a per-frame job — the AI
    /// re-queues a walk on its own schedule, and one clear a few times a second is enough to
    /// keep it planted without contesting anything else in the queue.</summary>
    private const int IntervalFrames = 10;

    private int _countdown;

    /// <summary>Cached resolution per slot, so a full tag sweep is not run every pass. Both
    /// the id and the tag are re-verified before use — a pointer alone is a stale-memory
    /// hazard the creature finder has already been bitten by.</summary>
    private readonly nint[] _creature = new nint[SharedBlock.InteractionLockCapacity];
    // Stored while the body is live. Never read an id through a cached pointer: a
    // cutscene can destroy that body before this lock's next pass.
    private readonly uint[] _creatureId = new uint[SharedBlock.InteractionLockCapacity];
    private readonly byte[][] _cachedTag = CreateTagCache();

    private static byte[][] CreateTagCache()
    {
        byte[][] tags = new byte[SharedBlock.InteractionLockCapacity][];
        for (int i = 0; i < tags.Length; i++)
        {
            tags[i] = new byte[SharedBlock.InteractionLockTagLength];
        }

        return tags;
    }

    private int _heldLogged = -1;

    // ---- containers (protocol version 37, docs/research/container-sync.md) -------------
    //
    // The Container-kind sibling of everything above: a peer's Container claim carries a
    // placeable object id (SharedBlockView.InteractionLockTargetId), not a tag, so it is
    // resolved via Odyssey.ResolvePlaceable rather than the creature-tag sweep, and the "hold"
    // it applies is a one-shot open/close ANIMATION at the edges of the lease rather than a
    // per-frame ClearAllActions.

    /// <summary>Mirrors <c>InteractionKind.Container</c>; the plugin does not reference the
    /// protocol assembly (same reasoning as <see cref="InteractionKindConversation"/>).</summary>
    private const uint InteractionKindContainer = 2;

    /// <summary>Containers a PEER currently holds, this pass — refreshed every
    /// <see cref="ApplyPeerLocks"/> and handed to <see cref="ContainerUseHook"/> so this
    /// instance's own attempt to open the same one is refused inside the engine.</summary>
    private readonly uint[] _peerContainerIds = new uint[SharedBlock.InteractionLockCapacity];
    private readonly uint[] _peerContainerPeerIds = new uint[SharedBlock.InteractionLockCapacity];

    /// <summary>Containers THIS instance has already played the open animation for, on a
    /// peer's behalf. Diffed against the current peer-held set every pass: new entries get the
    /// open animation, entries that drop out get the close animation — see
    /// <see cref="ReconcileContainerAnimations"/>.</summary>
    private readonly uint[] _mirroredOpenContainerIds = new uint[SharedBlock.InteractionLockCapacity];
    private int _mirroredOpenContainerCount;

    /// <summary>An allowed <c>OpenInventory</c> attempt this instance's own engine made,
    /// waiting for <c>Odyssey.IsPlaceableOpen</c> to confirm it actually opened (the function
    /// has its own internal no-op branches — see <see cref="ContainerUseHook"/> — so an
    /// attempt is not yet a claim).</summary>
    private uint _pendingContainerId;
    private uint _seenAttemptSerial;
    private int _pendingFramesLeft;

    /// <summary>How many <see cref="OnFrame"/> passes to keep watching a pending attempt
    /// before giving up. <see cref="PollLocalContainerUse"/> runs every frame (untimed, like
    /// <see cref="PublishLocalClaim"/> itself), so this is a frame count, not a duration —
    /// generous enough that a real open (which runs synchronously inside the same engine call
    /// that produced the attempt) is never missed, short enough that a declined attempt does
    /// not poll forever.</summary>
    private const int PendingContainerTimeoutFrames = 90;

    /// <summary>The container THIS instance's own local player currently has open, or 0.</summary>
    private uint _localContainerId;
    private bool _localContainerActive;

    /// <summary>
    /// ⚠ FIX (round 78, docs/research/container-sync.md's dated section): the id of the
    /// container this instance just RELEASED, held so <see cref="PublishLocalClaim"/> can keep
    /// naming it (kind=Container, targetId=this, active=false) instead of blanking the whole
    /// identity to "nothing happening" the instant the close is detected.
    ///
    /// Without this, a release was UNIDENTIFIABLE on the wire: <c>CoopHost</c>'s own claim
    /// guard and <c>InteractionLockStore.TryKey</c> both require a kind+id (or a tag) to know
    /// WHAT is being released, and a blanked identity made every close silently indistinguishable
    /// from "the player was never interacting with anything" — the release was dropped before it
    /// ever reached the lock store, and the only thing that ever cleared the stale entry was the
    /// 5s lease expiring on its own (<see cref="KotorCoop.Session.InteractionLockStore.DefaultLeaseMs"/>).
    /// LIVE-MEASURED: every close took ~5.0-5.1s to reach the peer, byte for byte the lease
    /// duration, regardless of <c>container_fast_pump</c> — that setting's own channel
    /// (<c>PumpPlaceables</c>) never carries a close at all (see <c>PlaceableController.Apply</c>,
    /// which only ever OPENS), so it could not have been the bottleneck the delay was measured
    /// against.
    ///
    /// Cleared the moment a NEW hold starts (superseded, nothing stale left to carry) and on
    /// <see cref="Invalidate"/> (a module change reassigns every id). Left set indefinitely
    /// otherwise — republishing an already-released id is a no-op on the far side
    /// (<c>ContainerUseRule.MayRelease</c> requires a FRESH current holder, and the entry is
    /// already gone after the first successful release), so there is no "stale forever" hazard
    /// to guard against with a one-shot flag.
    /// </summary>
    private uint _lastClosedContainerId;
    private uint _seenCloseSerial;

    private uint _seenBlockedCount;
    private long _containerNoticeUntilMs;
    private readonly byte[] _containerNoticeBuffer = new byte[SharedBlock.NameLength + ContainerNoticeSuffix.Length];
    private int _containerNoticeLength;

    private static readonly byte[] ContainerNoticeSuffix = " is looting that - wait a moment"u8.ToArray();
    private static readonly byte[] ContainerNoticeGenericName = "Another player"u8.ToArray();

    /// <summary>A module change reassigns every body, so a cached pointer names nothing.</summary>
    public void Invalidate()
    {
        for (int i = 0; i < SharedBlock.InteractionLockCapacity; i++)
        {
            _creature[i] = 0;
            _cachedTag[i][0] = 0;
            _nextRetryMs[i] = 0;
        }

        *block.LocalInteractionTag = 0;
        block.LocalInteractionActive = false;
        block.LocalInteractionKind = 0;
        block.LocalInteractionTargetId = 0;
        _heldLogged = -1;
        _farNoticeUntilMs = 0;

        // The OLD module's placeables are gone; a mirrored-open id names nothing there, so
        // this forgets rather than trying to play a close animation nobody will ever see —
        // same discipline the creature cache above already follows.
        _mirroredOpenContainerCount = 0;
        _pendingContainerId = 0;
        _seenAttemptSerial = 0;
        _pendingFramesLeft = 0;
        _localContainerId = 0;
        _localContainerActive = false;
        _lastClosedContainerId = 0;
        _seenBlockedCount = 0;
        _containerNoticeUntilMs = 0;
        _containerNoticeLength = 0;

        InvalidateContainerArbitration();
    }

    /// <summary>
    /// The session ended: stop holding creatures still on behalf of players who are gone.
    ///
    /// ⚠ THIS PASS HAS NO COMMAND GATE, which is deliberate (a lock has to survive the frames
    /// around a transition) and is exactly why it needed releasing explicitly. A lock left
    /// standing keeps calling ClearAllActions on the named creature several times a second
    /// forever — an NPC, quite possibly a companion, that stands there ignoring every order
    /// the player gives it. The agent now lowers the count on teardown as well; this covers
    /// the case where it never got the chance.
    /// </summary>
    public void ReleaseSession()
    {
        // A courtesy, not a correctness requirement: play the close pose on anything this
        // instance mirrored open on a peer's behalf, so a clean disconnect does not leave a
        // container looking open forever on THIS screen even though the session that was
        // holding it is gone. Invalidate() below forgets the tracking either way (the same
        // "module change reassigns everything" discipline applies to "session ended" too), so
        // this has to run first.
        for (int i = 0; i < _mirroredOpenContainerCount; i++)
        {
            nint placeable = Odyssey.ResolvePlaceable(_mirroredOpenContainerIds[i]);
            if (placeable == 0)
            {
                continue;
            }

            OdysseyPlaceables.PlayContainerCloseAnimation(placeable);
            OdysseyPlaceables.ClearPlaceableOpenFlag(placeable);
        }

        block.ClearInteractionLocks();
        Invalidate();
        _lockCountLogged = -1;
        ContainerUseHook.SetHeldContainers([], []);
        PluginHost.Log("interaction: dropped every peer lock — nobody else is holding anything here");
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        PublishLocalClaim();

        // Drawn every frame, independent of ApplyPeerLocks's throttle — a notice that only
        // repaints once every ten frames flickers.
        DrawFarNotice();

        // Container refusals come straight off ContainerUseHook's own capture, which fires the
        // instant the engine reaches OpenInventory — polling it only on ApplyPeerLocks's
        // ten-frame cadence would show the notice up to a third of a second late.
        PollContainerRefusals();
        DrawContainerNotice();

        // Same reasoning: a deferred click's grant/deny has to be acted on within a couple of
        // frames (docs/roadmap-seamless.md Tier 1 item 4), not on the ten-frame cadence below.
        PollContainerArbitration();

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;
        ApplyPeerLocks();
    }

    /// <summary>
    /// Publishes the object the local player is interacting with.
    ///
    /// Today that is exactly the conversation partner, which the dialogue controller has
    /// already resolved into the block this frame — so this reads that rather than resolving
    /// it a second time, and the two can never disagree about who the player is talking to.
    /// <see cref="InteractionKindConversation"/> is the only kind that exists; containers and
    /// doors are already arbitrated by their own channels.
    /// </summary>
    private void PublishLocalClaim()
    {
        byte* partner = block.LocalDialoguePartnerTag;
        byte* claim = block.LocalInteractionTag;

        int i = 0;
        while (i < SharedBlock.LocalInteractionTagLength - 1
            && i < SharedBlock.LocalDialoguePartnerTagLength
            && partner[i] != 0)
        {
            claim[i] = partner[i];
            i++;
        }

        claim[i] = 0;

        if (i > 0)
        {
            // Talking takes priority defensively — the engine's own dialogue system and the
            // container GUI cannot both have the player's attention in practice, but a claim
            // naming the wrong kind is worse than one naming nothing, so this never lets a
            // stale container watch overwrite a real conversation claim.
            block.LocalInteractionKind = InteractionKindConversation;
            block.LocalInteractionActive = true;
            block.LocalInteractionTargetId = 0;
            return;
        }

        PollLocalContainerUse();

        if (_localContainerActive)
        {
            block.LocalInteractionKind = InteractionKindContainer;
            block.LocalInteractionActive = true;
            block.LocalInteractionTargetId = _localContainerId;

            // A fresh hold supersedes whatever this instance last released; nothing stale is
            // left to (harmlessly) keep naming.
            _lastClosedContainerId = 0;
            return;
        }

        // A LEVEL — STILL true after the round-78 fix below, and still the whole reason a lost
        // packet is not a correctness problem: the lease on the far side expires on its own
        // regardless. What changed is that a container release is no longer published as a
        // BLANK identity ("nothing happening", indistinguishable from "never interacted with
        // anything"), which is what made a close UNMATCHABLE against the claim it was supposed
        // to release (CoopHost.PublishInteractionClaim's own guard and
        // InteractionLockStore.TryKey both need a kind+id to find the entry) and forced every
        // close to fall all the way back to the 5s lease expiry — LIVE-MEASURED, round 78,
        // container-sync.md's dated section. Naming the id here is an OPTIMISATION on top of
        // the same lease, not a replacement for it: losing this packet costs exactly the 5s it
        // always cost, nothing more.
        block.LocalInteractionKind = _lastClosedContainerId != 0 ? InteractionKindContainer : 0u;
        block.LocalInteractionActive = false;
        block.LocalInteractionTargetId = _lastClosedContainerId;
    }

    /// <summary>
    /// Detects "the local player is now looking at container C's loot panel" and "the local
    /// player closed it again", purely by correlating <see cref="ContainerUseHook"/>'s own
    /// capture of an ALLOWED <c>OpenInventory</c> attempt with <c>Odyssey.IsPlaceableOpen</c>
    /// actually flipping — no engine call of our own, and no guess: the same field the real
    /// <c>OpenInventory</c>/<c>CloseInventory</c> functions themselves set and clear (see
    /// <c>OdysseyPlaceables</c>'s own remarks on that field's real, transient semantics).
    /// </summary>
    private void PollLocalContainerUse()
    {
        uint attemptId = ContainerUseHook.LatestAttempt(out uint serial);
        uint closedId = ContainerCloseHook.LatestLocalClose(out uint closeSerial, out uint closeAttemptSerial);
        if (closeSerial != 0 && closeSerial != _seenCloseSerial)
        {
            _seenCloseSerial = closeSerial;
            // Do not interpret a stale canonical reopen as completion of the
            // older use attempt that the player already closed. A genuinely
            // later local OpenInventory has a new attempt serial and proceeds.
            if (_pendingContainerId == closedId && serial == closeAttemptSerial)
                _pendingContainerId = 0;
            if (_localContainerActive && _localContainerId == closedId && serial == closeAttemptSerial)
            {
                _lastClosedContainerId = closedId;
                _localContainerActive = false;
                _localContainerId = 0;
            }
        }
        if (serial != 0 && serial != _seenAttemptSerial)
        {
            _seenAttemptSerial = serial;
            if (attemptId != 0 && attemptId != _localContainerId)
            {
                _pendingContainerId = attemptId;
                _pendingFramesLeft = PendingContainerTimeoutFrames;
            }
        }

        if (_pendingContainerId != 0)
        {
            nint pending = Odyssey.ResolvePlaceable(_pendingContainerId);
            if (pending != 0 && Odyssey.IsPlaceableOpen(pending))
            {
                _localContainerId = _pendingContainerId;
                _localContainerActive = true;
                PlaceableLocalChanges.Record(_localContainerId, true);
                _pendingContainerId = 0;
            }
            else if (--_pendingFramesLeft <= 0)
            {
                // OpenInventory's own internal no-op branches (already open with no message
                // context, the "+0x324 not set" gate, ...) declined silently — give up
                // watching rather than poll forever.
                _pendingContainerId = 0;
            }
        }

        if (_localContainerActive)
        {
            nint held = Odyssey.ResolvePlaceable(_localContainerId);
            if (held == 0 || !Odyssey.IsPlaceableOpen(held))
            {
                // The player closed their own panel, or the module changed under us. Remember
                // WHICH one so the release itself can still be published — see
                // _lastClosedContainerId's own remarks.
                _lastClosedContainerId = _localContainerId;
                _localContainerActive = false;
                _localContainerId = 0;
            }
        }
    }

    /// <summary>Mirrors <c>InteractionKind.Conversation</c>; the plugin does not reference the
    /// protocol assembly.</summary>
    private const uint InteractionKindConversation = 1;

    /// <summary>
    /// Holds still every object a peer has claimed. The agent has already filtered our own
    /// claim out, so everything here belongs to somebody else.
    /// </summary>
    private void ApplyPeerLocks()
    {
        uint count = block.InteractionLockCount;
        int held = 0;
        int containerCount = 0;

        for (int i = 0; i < SharedBlock.InteractionLockCapacity; i++)
        {
            if (i >= count)
            {
                _creature[i] = 0;
                _cachedTag[i][0] = 0;
                _nextRetryMs[i] = 0;
                continue;
            }

            byte* entry = block.InteractionLock(i);
            uint kind = *(uint*)(entry + 4);

            if (kind == InteractionKindContainer)
            {
                // Not a creature; the tag-resolve cache below names nothing for this slot.
                _creature[i] = 0;
                _cachedTag[i][0] = 0;
                _nextRetryMs[i] = 0;

                uint targetId = block.InteractionLockTargetId(i);
                if (targetId != 0 && containerCount < _peerContainerIds.Length)
                {
                    _peerContainerIds[containerCount] = targetId;
                    _peerContainerPeerIds[containerCount] = *(uint*)entry; // peerId @0
                    containerCount++;
                }

                continue;
            }

            byte* tag = entry + 8;

            if (*tag == 0)
            {
                _creature[i] = 0;
                _cachedTag[i][0] = 0;
                _nextRetryMs[i] = 0;
                continue;
            }

            nint creature = Resolve(i, tag);
            if (creature == 0)
            {
                continue;
            }

            if (*(nint*)creature == CreatureFinder.CreatureVtable
                && Odyssey.HoldCreatureStill(creature))
            {
                held++;
            }

            TryForceOpen(entry, tag, creature);
        }

        ReconcileContainerAnimations(containerCount);
        ContainerUseHook.SetHeldContainers(
            new ReadOnlySpan<uint>(_peerContainerIds, 0, containerCount),
            new ReadOnlySpan<uint>(_peerContainerPeerIds, 0, containerCount));

        // ⚠ The COUNT is what says the channel works; the HELD count does not.
        //
        // HoldCreatureStill only reports true when it actually cleared a walk, so an NPC that
        // is standing still produces held == 0 — indistinguishable, in a log keyed on that
        // alone, from a lock that never arrived. The first live run said "nothing held for a
        // peer" and could not tell those apart.
        if (count != _lockCountLogged || held != _heldLogged)
        {
            _lockCountLogged = (int)count;
            _heldLogged = held;
            PluginHost.Log(count == 0
                ? "interaction: no peer holds anything here"
                : $"interaction: {count} object(s) held by a peer; stopped {held} of them walking");
        }
    }

    private int _lockCountLogged = -1;

    /// <summary>Reused across passes rather than allocated fresh — <see cref="ReconcileContainerAnimations"/>
    /// runs every <see cref="IntervalFrames"/> frames, not per-frame, but there is still no
    /// reason to churn two lists thirty times a minute.</summary>
    private readonly List<uint> _toOpen = [];
    private readonly List<uint> _toClose = [];

    /// <summary>
    /// Plays the container open/close animation exactly once per edge. The DECISION — which ids
    /// are newly held and which dropped out — is <c>KotorCoop.Protocol.ContainerAnimationEdges.Diff</c>,
    /// a pure function tested directly (this class is x86 NativeAOT and nothing in the test
    /// project references it, the same reason <c>DialogueAutoJoinRule</c> lives outside it);
    /// everything here is only the live engine calls that decision result implies.
    /// </summary>
    private void ReconcileContainerAnimations(int currentCount)
    {
        ContainerAnimationEdges.Diff(
            new ArraySegment<uint>(_peerContainerIds, 0, currentCount),
            new ArraySegment<uint>(_mirroredOpenContainerIds, 0, _mirroredOpenContainerCount),
            _toOpen,
            _toClose);

        foreach (uint id in _toOpen)
        {
            nint placeable = Odyssey.ResolvePlaceable(id);
            if (placeable == 0)
            {
                // Not resolvable yet (streaming in, or a stale/foreign id from a module we no
                // longer share) — retried next pass rather than recorded as mirrored, so a
                // late resolve still gets its open animation.
                continue;
            }

            if (!Odyssey.IsPlaceableOpen(placeable))
            {
                OdysseyPlaceables.PlayContainerOpenAnimation(placeable);
                Odyssey.SetPlaceableOpen(placeable);
            }

            if (_mirroredOpenContainerCount < _mirroredOpenContainerIds.Length)
            {
                _mirroredOpenContainerIds[_mirroredOpenContainerCount++] = id;
            }
        }

        foreach (uint id in _toClose)
        {
            nint placeable = Odyssey.ResolvePlaceable(id);
            if (placeable != 0)
            {
                OdysseyPlaceables.PlayContainerCloseAnimation(placeable);
                OdysseyPlaceables.ClearPlaceableOpenFlag(placeable);
            }

            RemoveMirrored(id);
        }
    }

    /// <summary>Swap-remove: order within this small tracking array carries no meaning.</summary>
    private void RemoveMirrored(uint id)
    {
        for (int i = 0; i < _mirroredOpenContainerCount; i++)
        {
            if (_mirroredOpenContainerIds[i] != id)
            {
                continue;
            }

            _mirroredOpenContainerCount--;
            _mirroredOpenContainerIds[i] = _mirroredOpenContainerIds[_mirroredOpenContainerCount];
            return;
        }
    }

    /// <summary>
    /// Notices a fresh refusal from <see cref="ContainerUseHook"/> and composes the on-screen
    /// line — "&lt;name&gt; is looting that - wait a moment", falling back to "Another player"
    /// when the holder's slot cannot be found (a peer that just disconnected, say). Composed
    /// once per refusal rather than every frame: the peer-name scan is cheap but there is no
    /// reason to repeat it thirty times a second for a notice that only changes on an edge.
    /// </summary>
    private void PollContainerRefusals()
    {
        uint blocked = ContainerUseHook.BlockedCount;
        if (blocked == _seenBlockedCount)
        {
            return;
        }

        _seenBlockedCount = blocked;
        _containerNoticeUntilMs = Environment.TickCount64 + FarNoticeDurationMs;
        (uint containerId, uint holderPeerId) = ContainerUseHook.LastBlocked;
        _containerNoticeLength = BuildContainerNoticeText(holderPeerId);

        // ⚠ Every other refusal path in this plugin logs (CombatController, ItemController,
        // TransitionAnnounceWatch's door-refusal, ...); ContainerUseHook's own callback stayed
        // silent (the hot-path OpenInventory call is no place to add a managed I/O call on
        // every ALLOWED attempt too), so this — the one place a refusal is already singled out
        // from the noise, once per edge — is where the live-test/verify.ps1 evidence trail
        // needs a line to grep for.
        PluginHost.Log($"container: refused open of 0x{containerId:X} (held by peer {holderPeerId})");
    }

    private int BuildContainerNoticeText(uint holderPeerId)
    {
        int n = 0;
        uint remoteCount = Math.Min(block.RemoteCount, (uint)SharedBlock.SlotCapacity);

        for (int i = 0; i < remoteCount; i++)
        {
            RemoteView remote = block.GetRemote(i);
            if (!remote.Active || remote.PeerId != holderPeerId)
            {
                continue;
            }

            byte* name = block.GetRemoteName(i);
            int cap = _containerNoticeBuffer.Length - ContainerNoticeSuffix.Length;
            while (n < cap && n < SharedBlock.NameLength && name[n] != 0)
            {
                _containerNoticeBuffer[n] = name[n];
                n++;
            }

            break;
        }

        if (n == 0)
        {
            foreach (byte b in ContainerNoticeGenericName)
            {
                _containerNoticeBuffer[n++] = b;
            }
        }

        foreach (byte b in ContainerNoticeSuffix)
        {
            if (n >= _containerNoticeBuffer.Length)
            {
                break;
            }

            _containerNoticeBuffer[n++] = b;
        }

        return n;
    }

    /// <summary>Row 7: <see cref="DrawFarNotice"/> already claims row 6,
    /// <c>DialogueSpectateController</c>'s own notice sits at row 4 and
    /// <c>AgentLivenessController</c>'s at row 5 — see <see cref="DrawFarNotice"/>'s own remarks
    /// for why each of these lives on its own row.</summary>
    private void DrawContainerNotice()
    {
        if (Environment.TickCount64 >= _containerNoticeUntilMs || _containerNoticeLength == 0)
        {
            return;
        }

        fixed (byte* p = _containerNoticeBuffer)
        {
            Odyssey.PostTextLine(p, 8, 7, 0.5f);
        }
    }

    /// <summary>
    /// Asks the engine to open the conversation a peer is holding this NPC for, so both
    /// players are in it without the second one having to click — <c>dialogue_auto_join</c>'s
    /// live half. The decision itself is <see cref="DialogueAutoJoinRule.ShouldAutoJoin"/>
    /// (pure, tested); everything here is only gathering the facts that rule needs from the
    /// live world and acting on its answer.
    ///
    /// Nothing here forces anything by itself: on a join, it sends the exact same request a
    /// click sends. The dialogue gate lets it through because the target IS the peer's
    /// partner, the engine opens the same conversation locally, and
    /// DialogueSpectateController's adopt path turns it into a follow — the identical route a
    /// real click takes. That is deliberate: a second way into a conversation would be a
    /// second thing to keep working.
    ///
    /// Symmetric by construction: this runs identically on every peer (see
    /// <see cref="DialogueAutoJoinRule"/>'s own header), so a host deciding whether to join a
    /// CLIENT's conversation and a client deciding whether to join the HOST's run the exact
    /// same code with no branch on which side started it.
    /// </summary>
    private void TryForceOpen(byte* entry, byte* tag, nint partnerCreature)
    {
        if (*(uint*)(entry + 4) != InteractionKindConversation)
        {
            return;
        }

        int length = 0;
        while (length < SharedBlock.InteractionLockTagLength - 1 && tag[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return;
        }

        // Item 1 (docs/research/dialogue-sync.md's 2026-08-23 section): a conversation the
        // vanilla engine itself forces regardless of distance auto-joins UNCONDITIONALLY, even
        // under dialogue_auto_join=off — that is matching behaviour vanilla already forces onto
        // a single-player PC, not inventing a new one. Sourced from the session's single
        // arbitrated dialogue token (block.RemoteDialogue*), not this interaction lock entry
        // (a courtesy mirror published slightly earlier, before the token itself resolves) —
        // the tag match confirms THIS lock entry names the same conversation the token
        // describes, so a transient mismatch during the token's own resolve window degrades to
        // "not forced yet" rather than a wrong answer.
        bool isForced = block.RemoteDialogueActive && block.RemoteDialogueForced
            && TagEquals(tag, length, block.RemoteDialoguePartnerTag);

        if (_autoJoinMode == DialogueAutoJoinMode.Off && !isForced)
        {
            return;
        }

        // Already talking to somebody — including, most of the time, this very conversation.
        bool localAlreadyInConversation = Odyssey.IsConversationActive;

        float distance = float.PositiveInfinity;
        nint player = Odyssey.GetPlayerCreaturePointer();
        if (player != 0 && partnerCreature != 0)
        {
            Vec3 a = Odyssey.GetPosition(player);
            Vec3 b = Odyssey.GetPosition(partnerCreature);
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        bool shouldJoin = DialogueAutoJoinRule.ShouldAutoJoin(
            _autoJoinMode,
            isForced,
            localAlreadyInConversation,
            Odyssey.IsPlayerInCombat,
            MinigameController.LocalInMinigame,
            distance,
            _autoJoinRange);

        if (!shouldJoin)
        {
            // "Too far, but otherwise would have joined" is the ONLY case worth telling the
            // player about — not already-in-a-conversation (they already know), not
            // combat/minigame (the screen already says so). Near-mode's whole reason to exist
            // is exactly this notice: the player is not stuck, they can walk over. A forced
            // conversation never reaches this branch (ShouldAutoJoin returns true unconditionally
            // once safety-gated), so it never needs the notice either.
            if (_autoJoinMode == DialogueAutoJoinMode.Near
                && !localAlreadyInConversation && !Odyssey.IsPlayerInCombat
                && !MinigameController.LocalInMinigame && float.IsFinite(distance))
            {
                _farNoticeUntilMs = Environment.TickCount64 + FarNoticeDurationMs;
            }

            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextForceOpenMs)
        {
            return;
        }

        _nextForceOpenMs = now + ForceOpenRetryMs;

        int resrefLength = 0;
        while (resrefLength < 16 && block.RemoteDialogueResref[resrefLength] != 0) resrefLength++;
        string resref = block.RemoteDialogueActive
            && TagEquals(tag, length, block.RemoteDialoguePartnerTag)
            ? System.Text.Encoding.ASCII.GetString(block.RemoteDialogueResref, resrefLength)
            : string.Empty;
        if (Odyssey.StartDialogueWith(new ReadOnlySpan<byte>(tag, length), resref,
                isForced, partnerCreature))
        {
            PluginHost.Log("interaction: auto-joining a peer's conversation with "
                + $"'{System.Text.Encoding.ASCII.GetString(tag, length)}' "
                + (isForced
                    ? "(forced — matching the engine's own force)"
                    : $"(dialogue_auto_join={_autoJoinMode})"));
        }
    }

    /// <summary>Case-insensitive compare of a length-prefixed tag against a null-terminated one
    /// — both operands here are OUR OWN block fields (written by <c>TryWriteAsciiString</c>),
    /// never raw engine memory, so this needs none of <c>DialogueGateHook</c>'s CExoString
    /// length-includes-null handling.</summary>
    private static bool TagEquals(byte* tag, int length, byte* nulTerminated)
    {
        for (int i = 0; i < length; i++)
        {
            byte a = tag[i];
            byte b = nulTerminated[i];
            if (b == 0 || Lower(a) != Lower(b))
            {
                return false;
            }
        }

        return nulTerminated[length] == 0;
    }

    private static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;

    /// <summary>How long the "walk over to join" notice stays on screen after being triggered
    /// — long enough to read, short enough not to linger once the player has moved on.</summary>
    private const long FarNoticeDurationMs = 3000;

    private long _farNoticeUntilMs;

    private static readonly byte[] FarNotice =
        "A peer is talking nearby - walk over to join"u8.ToArray();

    /// <summary>Draws the walk-over notice while it is live. Reuses the queued text path every
    /// other on-screen line in this codebase does (<see cref="Odyssey.PostTextLine"/>) — see
    /// docs/research/text-render-queued.md.
    ///
    /// Row 6, deliberately: <see cref="DialogueSpectateController"/>'s own "click to join"
    /// notice sits at (8, 4) and <c>AgentLivenessController</c>'s stale-agent message sits at
    /// (3, 5) — same row, different column, close enough that a long AgentLiveness message
    /// could run into this one if both shared a row. Row 6 clears both.</summary>
    private void DrawFarNotice()
    {
        if (Environment.TickCount64 >= _farNoticeUntilMs)
        {
            return;
        }

        fixed (byte* p = FarNotice)
        {
            Odyssey.PostTextLine(p, 8, 6, 0.5f);
        }
    }

    /// <summary>
    /// The live object a lock names, cached across passes. Resolve its stored id before
    /// dereferencing the cache, then recheck its tag; cutscenes may destroy the body.
    /// </summary>
    private nint Resolve(int slot, byte* tag)
    {
        int length = 0;
        while (length < SharedBlock.InteractionLockTagLength - 1 && tag[length] != 0)
        {
            length++;
        }

        ReadOnlySpan<byte> wanted = new(tag, length);
        bool sameTag = SameCachedTag(slot, wanted);

        if (sameTag && _creature[slot] != 0)
        {
            nint cached = _creature[slot];
            if (Odyssey.ResolveServerObject(_creatureId[slot]) == cached
                && Odyssey.HasTag(cached, wanted))
            {
                return cached;
            }
        }

        // Keep a short miss cache for objects still streaming in. This used to require a
        // full heap sweep and a two-second backoff. Live object-table lookup is bounded
        // and cheap enough to retry promptly without stalling the game thread.
        if (sameTag && _creature[slot] == 0 && Environment.TickCount64 < _nextRetryMs[slot])
        {
            return 0;
        }

        nint found = Odyssey.FindDialoguePartnerByTag(wanted);
        _creature[slot] = found;
        _creatureId[slot] = found == 0 ? 0 : *(uint*)(found + 4);
        _nextRetryMs[slot] = found != 0 ? 0 : Environment.TickCount64 + MissRetryMs;

        for (int i = 0; i < length; i++)
        {
            _cachedTag[slot][i] = tag[i];
        }

        _cachedTag[slot][length] = 0;
        return found;
    }

    /// <summary>Backoff for a partner that has not yet streamed into the object table.</summary>
    private const long MissRetryMs = 250;

    private readonly long[] _nextRetryMs = new long[SharedBlock.InteractionLockCapacity];

    private bool SameCachedTag(int slot, ReadOnlySpan<byte> wanted)
    {
        byte[] cached = _cachedTag[slot];
        for (int i = 0; i < wanted.Length; i++)
        {
            if (cached[i] != wanted[i])
            {
                return false;
            }
        }

        return cached[wanted.Length] == 0;
    }
}
