using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — the mirrored per-companion experience level, plus this client's own reading
/// reported to the host every tick. See CoopHost.CompanionProgression.cs's host half and
/// docs/research/companion-state-sync.md.
/// </summary>
public sealed partial class CoopClient
{
    private ushort _companionXpMask;
    private uint[] _companionXp = new uint[ProtocolConstants.PartyRosterSlots];
    private bool _hasCompanionXp;

    /// <summary>Only the host is authoritative for companion experience; a client's own view
    /// is dropped.</summary>
    public void PublishCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot)
    {
    }

    public bool TryGetCompanionExperience(out ushort presentMask, out uint[] xpBySlot)
    {
        lock (_gate)
        {
            presentMask = _companionXpMask;
            xpBySlot = _companionXp;
            return _hasCompanionXp;
        }
    }

    public void ReportCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot)
    {
        if (_localPeerId == 0)
        {
            return;
        }

        uint[] xp = new uint[ProtocolConstants.PartyRosterSlots];
        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            xp[slot] = slot < xpBySlot.Count ? xpBySlot[slot] : 0;
        }

        // Sent every tick, unreliable, same discipline as ReportProgression: this is a LEVEL
        // (this peer's current reading), not an event, so a lost datagram costs a tick and a
        // repeat applies nothing extra.
        int length = PacketCodec.Write(
            _sendBuffer, new CompanionXpReportPacket(_localPeerId, presentMask, xp));

        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>The host merges reports; a client only sends its own.</summary>
    public IReadOnlyList<PeerCompanionXp> GetCompanionExperienceReports() => [];

    private void HandleCompanionXpSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCompanionXpSnapshot(span) is { } snapshot)
        {
            lock (_gate)
            {
                _companionXpMask = snapshot.PresentMask;
                _companionXp = snapshot.Xp;
                _hasCompanionXp = true;
            }
        }
    }
}
