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
internal sealed unsafe class InteractionLockController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("interaction_locks", true);

    /// <summary>
    /// Pull this player into a conversation another player has started with an NPC, instead of
    /// waiting for them to click the same NPC themselves.
    ///
    /// ⚠ DEFAULT OFF, and it should stay off until two things are settled.
    ///
    /// First, it is the behaviour that was already removed once. An earlier build opened the
    /// peer's conversation by itself and players were yanked out of what they were doing from
    /// anywhere in the module; joining became a click precisely because walking away while
    /// your partner talks to a shopkeeper is a legitimate thing to want.
    ///
    /// Second, and unresolved: a conversation that RECRUITS or DISMISSES a companion calls
    /// AddPartyMember / RemovePartyMember, and those have no channel in this mod at all. A
    /// player driven into a recruitment conversation by this flag ends it with a party roster
    /// the other instance does not share, and the roster is what the companion channel keys
    /// on. Turning this on for ordinary conversation is fine; turning it on and then walking
    /// into Trask's recruitment is not.
    /// </summary>
    private readonly bool _forceDialogue = PluginConfig.ReadBool("force_dialogue_party", false);

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
        _heldLogged = -1;
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
        block.ClearInteractionLocks();
        Invalidate();
        _lockCountLogged = -1;
        PluginHost.Log("interaction: dropped every peer lock — nobody else is holding anything here");
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        PublishLocalClaim();

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

        block.LocalInteractionKind = i > 0 ? InteractionKindConversation : 0;

        // A LEVEL. The lease on the far side expires on its own when this stops being true,
        // which is the whole reason there is no release message to lose.
        block.LocalInteractionActive = i > 0;
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

            if (Odyssey.HoldCreatureStill(creature))
            {
                held++;
            }

            TryForceOpen(entry, tag);
        }

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

    /// <summary>
    /// Asks the engine to open the conversation a peer is holding this NPC for, so both
    /// players are in it without the second one having to click.
    ///
    /// Nothing here forces anything by itself: it sends the same request a click sends. The
    /// dialogue gate lets it through because the target IS the peer's partner, the engine
    /// opens the same conversation locally, and DialogueSpectateController's adopt path turns
    /// it into a follow — the identical route a real click takes. That is deliberate: a
    /// second way into a conversation would be a second thing to keep working.
    /// </summary>
    private void TryForceOpen(byte* entry, byte* tag)
    {
        if (!_forceDialogue || *(uint*)(entry + 4) != InteractionKindConversation)
        {
            return;
        }

        // Already talking to somebody — including, most of the time, this very conversation.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextForceOpenMs)
        {
            return;
        }

        _nextForceOpenMs = now + ForceOpenRetryMs;

        int length = 0;
        while (length < SharedBlock.InteractionLockTagLength - 1 && tag[length] != 0)
        {
            length++;
        }

        if (length > 0 && Odyssey.StartDialogueWith(new ReadOnlySpan<byte>(tag, length)))
        {
            PluginHost.Log("interaction: joining a peer's conversation with "
                + $"'{System.Text.Encoding.ASCII.GetString(tag, length)}' (force_dialogue_party)");
        }
    }

    /// <summary>
    /// The creature a lock names, cached across passes but re-verified every time: the tag
    /// must still match the one we resolved, and the body must still be a creature carrying
    /// that tag. A cached pointer whose memory has been reused is the failure mode the
    /// creature finder has already paid for once.
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
            if (*(int*)(cached + 0x08) == 5 && Odyssey.HasTag(cached, wanted))
            {
                return cached;
            }
        }

        // ⚠ A FAILED LOOKUP MUST BE REMEMBERED, OR IT REPEATS FOREVER AT THE PASS RATE.
        //
        // CreatureFinder.FindByTag SWEEPS: it walks the whole user address space through
        // VirtualQuery, copying every committed private region out in 256 KB chunks and
        // testing every 4-byte-aligned dword. A sweep that SUCCEEDS returns at the first
        // match; a sweep that FAILS does all of it, every time. Failure is therefore the
        // expensive case by construction, which is the opposite of the intuition.
        //
        // And failure is the NORMAL case here: a lock names an object the peer is using, and
        // the instance holding the lock is routinely one that has no local body for it. The
        // first version stored the 0 and the cache test rejected it on the next pass, so it
        // swept again — about thirty full address-space sweeps a second, on the frame thread.
        //
        // So the tag is recorded whether or not the body was found, and a miss is retried on
        // a timer rather than immediately. Same remedy MaterialisedEnemies already needed for
        // the same finder (its ProvenPresent list, after a measured sweep storm).
        if (sameTag && _creature[slot] == 0 && Environment.TickCount64 < _nextRetryMs[slot])
        {
            return 0;
        }

        nint found = CreatureFinder.FindByTag(wanted);
        _creature[slot] = found;
        _nextRetryMs[slot] = found != 0 ? 0 : Environment.TickCount64 + MissRetryMs;

        for (int i = 0; i < length; i++)
        {
            _cachedTag[slot][i] = tag[i];
        }

        _cachedTag[slot][length] = 0;
        return found;
    }

    /// <summary>How long a failed tag lookup is trusted before sweeping again. Long enough
    /// that a miss costs nothing, short enough that a body which has since streamed in is
    /// picked up well within a conversation.</summary>
    private const long MissRetryMs = 2000;

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
