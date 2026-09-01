using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Phantom damage — combat this client's real fight deals to another player's
/// non-solid stand-in — kept as per-sender running totals (never a single max) so a
/// second attacker's contribution is never silently dropped.
/// </summary>
public sealed partial class CoopClient
{
    // ---- phantom damage (combat participation) -----------------------------------

    private readonly Dictionary<uint, PhantomDamageReport> _phantomDamageOut = [];

    /// <summary>Running totals owed to the local player, keyed by the peer that dealt them.
    /// See <see cref="GetIncomingPhantomDamage"/>: with three players two different peers can
    /// be hitting this player's phantom, and their totals add.</summary>
    private readonly Dictionary<uint, PhantomDamageReport> _phantomDamageIn = [];

    private uint _lastIncomingAttacker;

    /// <summary>Queued for the next tick's send; the host relays it to the addressee (or
    /// keeps it, when the host is the one being hit).</summary>
    public void PublishPhantomDamage(
        uint targetPeerId, uint cumulativeDamage, uint attackerId,
        IReadOnlyList<PhantomAttackerEntry>? attackers = null)
    {
        if (targetPeerId == 0 || targetPeerId == _localPeerId)
        {
            return;
        }

        lock (_gate)
        {
            _phantomDamageOut[targetPeerId] =
                new PhantomDamageReport(cumulativeDamage, attackerId, attackers ?? []);
        }
    }

    /// <summary>
    /// The total this player owes, summed across every peer that has hit their phantom.
    ///
    /// ⚠ Per-sender running totals ADD; they do not max. Keeping one number meant a second
    /// attacker whose total was below the leader's was ignored outright, forever. The sum is
    /// still monotonic, which is what the plugin's applied-so-far counter needs, so entries
    /// are never removed - dropping one would walk the total backwards.
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

    private void HandlePhantomDamage(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPhantomDamage(span) is not { } packet
            || packet.TargetPeerId != _localPeerId)
        {
            return;
        }

        lock (_gate)
        {
            // Monotonic PER SENDER, so a reordered datagram cannot walk that sender's total
            // backwards and make the plugin re-apply the difference. The host relays another
            // client's report with the ORIGINATOR in FromPeerId, so this key really does
            // separate the attackers rather than lumping everything under the host.
            if (_phantomDamageIn.TryGetValue(packet.FromPeerId, out PhantomDamageReport seen)
                && packet.CumulativeDamage <= seen.Damage)
            {
                return;
            }

            _phantomDamageIn[packet.FromPeerId] =
                new PhantomDamageReport(packet.CumulativeDamage, packet.AttackerId, packet.Attackers);

            if (packet.AttackerId != 0)
            {
                _lastIncomingAttacker = packet.AttackerId;
            }
        }
    }

    private void SendPhantomDamage()
    {
        List<KeyValuePair<uint, PhantomDamageReport>> owed;
        lock (_gate)
        {
            if (_phantomDamageOut.Count == 0 || _localPeerId == 0)
            {
                return;
            }

            owed = [.. _phantomDamageOut];
        }

        foreach ((uint targetPeerId, PhantomDamageReport report) in owed)
        {
            if (report.Damage == 0)
            {
                continue;
            }

            int length = PacketCodec.Write(
                _sendBuffer,
                new PhantomDamagePacket(_localPeerId, targetPeerId, report.Damage, report.Attacker, report.Attackers));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }
}
