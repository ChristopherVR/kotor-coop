using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Phantom damage — combat one player's real fight deals to another player's
/// non-solid stand-in — kept as per-sender running totals (never a single max) so a
/// second attacker's contribution is never silently dropped. See GetIncomingPhantomDamage.
/// </summary>
public sealed partial class CoopHost
{
    // ---- phantom damage (combat participation) -----------------------------------
    //
    // Running totals, keyed by the peer the damage is FOR. Sent to that peer every tick;
    // when the addressee is the host itself, kept here for the pump to read. Cumulative
    // and monotonic, so an out-of-order or repeated datagram can never double-apply.

    /// <summary>
    /// Keyed by (who it is FOR, who DEALT it). ⚠ The source half is not decoration: a total
    /// is per-sender, so folding two senders into one key means whichever wrote last wins and
    /// the other's damage is thrown away every tick. The host both originates reports (its own
    /// game hit a phantom) and relays them (a client's game did), so with three players two
    /// sources for the same target is the ordinary case.
    /// </summary>
    private readonly Dictionary<(uint Target, uint Source), PhantomDamageReport> _phantomDamageOut = [];

    /// <summary>Running totals owed to the local player, per attacking peer. See
    /// <see cref="GetIncomingPhantomDamage"/> for why this cannot be one number.</summary>
    private readonly Dictionary<uint, PhantomDamageReport> _phantomDamageIn = [];

    private uint _lastIncomingAttacker;

    public void PublishPhantomDamage(
        uint targetPeerId, uint cumulativeDamage, uint attackerId,
        IReadOnlyList<PhantomAttackerEntry>? attackers = null)
    {
        if (targetPeerId == 0 || targetPeerId == LocalPeerId)
        {
            return;
        }

        lock (_gate)
        {
            _phantomDamageOut[(targetPeerId, LocalPeerId)] =
                new PhantomDamageReport(cumulativeDamage, attackerId, attackers ?? []);
        }
    }

    /// <summary>
    /// The total this player owes, summed across everyone who has hit their phantom.
    ///
    /// ⚠ Each entry is a per-sender RUNNING TOTAL, so they must be added, not maxed. The
    /// old single-slot version kept only the largest total it had seen: once one peer had
    /// dealt 40, a second peer's reports of 5, 10, 15 were all smaller and every one of them
    /// was discarded - that player's damage simply never landed. Invisible with two players,
    /// because there is only ever one source.
    ///
    /// The sum stays monotonic (every term is), which is what the plugin's applied-so-far
    /// bookkeeping needs. Entries are therefore NOT dropped when a peer leaves: removing one
    /// would walk the total backwards.
    /// </summary>
    public (uint CumulativeDamage, uint AttackerId) GetIncomingPhantomDamage()
    {
        lock (_gate)
        {
            ulong total = 0;
            foreach (PhantomDamageReport report in _phantomDamageIn.Values)
            {
                total += report.Damage;
            }

            return ((uint)Math.Min(total, uint.MaxValue), _lastIncomingAttacker);
        }
    }

    /// <summary>The same total as <see cref="GetIncomingPhantomDamage"/>, split per attacker
    /// id and merged across every sender — see <see cref="PhantomAttackerMerge"/>.</summary>
    public IReadOnlyList<PhantomAttackerEntry> GetIncomingPhantomAttackers()
    {
        lock (_gate)
        {
            return PhantomAttackerMerge.Merge(
                _phantomDamageIn.Values.Select(report => report.Attackers),
                ProtocolConstants.MaxPhantomAttackerEntries);
        }
    }

    /// <summary>Records one sender's running total, ignoring a reordered datagram that would
    /// walk that sender's figure backwards.</summary>
    private void AcceptIncomingPhantomDamage(
        uint sourcePeerId, uint cumulative, uint attacker, IReadOnlyList<PhantomAttackerEntry> attackers)
    {
        if (_phantomDamageIn.TryGetValue(sourcePeerId, out PhantomDamageReport seen)
            && cumulative <= seen.Damage)
        {
            return;
        }

        _phantomDamageIn[sourcePeerId] = new PhantomDamageReport(cumulative, attacker, attackers);

        if (attacker != 0)
        {
            _lastIncomingAttacker = attacker;
        }
    }

    /// <summary>Drops a departed peer from the outbound table. The inbound side keeps its
    /// entry on purpose - see <see cref="GetIncomingPhantomDamage"/>.</summary>
    private void ForgetPhantomDamageFor(uint peerId)
    {
        List<(uint Target, uint Source)>? dead = null;
        foreach ((uint Target, uint Source) key in _phantomDamageOut.Keys)
        {
            if (key.Target == peerId || key.Source == peerId)
            {
                (dead ??= []).Add(key);
            }
        }

        if (dead is null)
        {
            return;
        }

        foreach ((uint Target, uint Source) key in dead)
        {
            _phantomDamageOut.Remove(key);
        }
    }

    /// <summary>A client reported damage its game did to someone's phantom. If it is for
    /// the host, keep it; otherwise it is relayed to the addressee on the next broadcast.</summary>
    private void HandlePhantomDamage(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPhantomDamage(span) is not { } packet)
        {
            return;
        }

        lock (_gate)
        {
            if (packet.TargetPeerId == LocalPeerId)
            {
                AcceptIncomingPhantomDamage(
                    packet.FromPeerId, packet.CumulativeDamage, packet.AttackerId, packet.Attackers);
            }
            else if (packet.TargetPeerId != 0)
            {
                // Relayed. The ORIGINATOR is kept as the key and re-sent as the From field,
                // so the addressee can tell two attackers apart; stamping the host's own id
                // on it would merge every relayed report into one running total.
                _phantomDamageOut[(packet.TargetPeerId, packet.FromPeerId)] =
                    new PhantomDamageReport(packet.CumulativeDamage, packet.AttackerId, packet.Attackers);
            }
        }
    }

    private void BroadcastPhantomDamage(List<PeerConnection> recipients)
    {
        List<KeyValuePair<(uint Target, uint Source), PhantomDamageReport>> owed;
        lock (_gate)
        {
            if (_phantomDamageOut.Count == 0)
            {
                return;
            }

            owed = [.. _phantomDamageOut];
        }

        // One datagram per (addressee, source) pair rather than per addressee: each carries
        // that source's own running total, which is what the receiver sums.
        foreach (PeerConnection peer in recipients)
        {
            foreach (((uint target, uint source), PhantomDamageReport report) in owed)
            {
                if (target != peer.Id || report.Damage == 0)
                {
                    continue;
                }

                int length = PacketCodec.Write(
                    _sendBuffer,
                    new PhantomDamagePacket(source, peer.Id, report.Damage, report.Attacker, report.Attackers));
                if (length > 0)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }
}
