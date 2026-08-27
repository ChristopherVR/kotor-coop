using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// Carries hits and non-damage effects BOTH ways for combat participation: what each peer's
// phantom has taken (or had land on it) in this game goes out to that peer, and what this
// player owes for something taken on their own phantom elsewhere goes into the plugin to be
// applied for real. This is what makes enemies dangerous to everyone rather than only to
// whoever owns them. See docs/research/peer-damage-ledger.md and docs/research/effect-sync.md.
internal sealed partial class CombatPump
{
    public void PumpPhantomDamage(SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        // Outbound: ask the bridge WHO is in slot i.
        //
        // ⚠ It used to index the peer list by slot number, with a comment claiming the two
        // orderings matched. They did not. The list written into the block is filtered by
        // module and masked by capability first, so it is a subsequence of GetRemotePeers();
        // slots are also sticky per peer, so a departure leaves a hole. Either alone is
        // enough to bill the wrong player for a hit — and to bill someone standing in a
        // different area entirely. Unreachable with two players (one remote peer means one
        // slot); routine with three.
        for (int slot = 0; slot < bridge.RemoteSlotCapacity; slot++)
        {
            uint peerId = bridge.PeerInSlot(slot);
            if (peerId == 0)
            {
                continue;
            }

            if (!bridge.TryReadPhantomHit(slot, out uint phantomId, out uint cumulative, out uint attacker)
                || phantomId == 0
                || cumulative == 0)
            {
                continue;
            }

            session.PublishPhantomDamage(peerId, cumulative, attacker);
        }

        // Inbound: hand the plugin the running total. Level state, written every tick —
        // the plugin tracks what it has already applied, so this cannot double-count.
        //
        // Skipped entirely while THIS player is in a minigame: they cannot be hit in a
        // turret, and there is no engine body standing in the shared world to apply it to.
        // The total already owed is not cleared here — the session's ledger keeps it, and
        // catching up once the write resumes is the same "no double-count, no drop" the
        // level-state design already gives every other consumer of GetIncomingPhantomDamage.
        // A hit taken elsewhere DURING a minigame therefore still lands once the player is
        // back in the world; see docs/research/minigames.md.
        // MinigameRule.ShouldSuppressIncomingDamage ignores both of its toggles by design
        // (see MinigameRuleTests) so a default instance answers it correctly.
        (uint owed, uint from) = session.GetIncomingPhantomDamage();
        if (owed > 0 && !default(MinigameRule).ShouldSuppressIncomingDamage(bridge.IsInMinigame))
        {
            bridge.TryWriteIncomingDamage(owed, from);
        }
    }

    /// <summary>The non-damage sibling of <see cref="PumpPhantomDamage"/>: stun, paralyze,
    /// knockdown, slow, frightened, sleep, choke, Force push/whirlwind landing on a peer's
    /// phantom here, forwarded to the player it stands for.
    ///
    /// Unlike phantom damage this is a small EVENT ring, not a running total — a single global
    /// high-water mark on the outbound side is enough dedup: the hook's serial only ever
    /// increases, and a burst larger than the ring's 8 entries between two pumps is exactly
    /// the "one crowd-control tick lost" cost the channel accepts by design (unreliable, like
    /// barks).
    /// </summary>
    private uint _publishedPeerEffectSerial;

    public void PumpPeerEffects(SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        // Outbound: PeerEffectRules.SelectNew is the pure "what changed" decision (unit-tested
        // in isolation); everything below it is just resolving WHO to send it to, the same
        // linkage PumpPhantomDamage relies on — TryReadPhantomHit publishes every watched
        // slot's phantom id each frame, hit or not.
        List<PeerEffectRecord> captured = bridge.ReadPeerEffectsOut();
        if (captured.Count > 0)
        {
            uint[] serials = new uint[captured.Count];
            for (int i = 0; i < captured.Count; i++)
            {
                serials[i] = captured[i].Serial;
            }

            (IReadOnlyList<uint> newIndices, uint newWatermark) =
                PeerEffectRules.SelectNew(serials, _publishedPeerEffectSerial);
            _publishedPeerEffectSerial = newWatermark;

            if (newIndices.Count > 0)
            {
                Dictionary<uint, uint> phantomToPeer = [];
                for (int slot = 0; slot < bridge.RemoteSlotCapacity; slot++)
                {
                    uint peerId = bridge.PeerInSlot(slot);
                    if (peerId != 0 && bridge.TryReadPhantomHit(slot, out uint phantomId, out _, out _)
                        && phantomId != 0)
                    {
                        phantomToPeer[phantomId] = peerId;
                    }
                }

                foreach (uint index in newIndices)
                {
                    PeerEffectRecord effect = captured[(int)index];
                    if (phantomToPeer.TryGetValue(effect.PhantomId, out uint peerId))
                    {
                        session.PublishPeerEffect(
                            peerId, (PeerEffectKind)effect.Kind, effect.DurationMs, effect.Magnitude);
                    }
                }
            }
        }

        // Inbound: hand the plugin whatever a peer's hook captured landing on OUR phantom
        // over there, oldest first, so it can replay them on the real character here.
        foreach (PeerEffectPacket effect in session.DrainPeerEffects())
        {
            bridge.TryWritePeerEffectIn(
                effect.FromPeerId, (uint)effect.Kind, effect.DurationMs, effect.Magnitude, effect.Sequence);
        }
    }
}
