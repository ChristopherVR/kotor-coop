using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — The mirrored shared gold/experience level, plus this client's own
/// contribution report sent to the host every tick.
/// </summary>
public sealed partial class CoopClient
{
    private uint _gold;
    private uint _experience;
    private bool _hasProgression;

    /// <summary>Only the host is authoritative for progression; a client's own view is dropped.</summary>
    public void PublishProgression(uint gold, uint experience)
    {
    }

    public bool TryGetProgression(out uint gold, out uint experience)
    {
        lock (_gate)
        {
            gold = _gold;
            experience = _experience;
            return _hasProgression;
        }
    }

    public void ReportProgression(uint generation, uint goldEarned, uint goldSpent, uint experience)
    {
        if (_localPeerId == 0)
        {
            return;
        }

        // Sent every tick even when nothing changed, and unreliable, for the same reason the
        // damage ledger is: the totals are cumulative, so a lost datagram costs a tick of
        // staleness rather than a purchase, and a repeat applies nothing.
        int length = PacketCodec.Write(
            _sendBuffer,
            new ProgressionReportPacket(_localPeerId, generation, goldEarned, goldSpent, experience));

        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>The host merges reports; a client only sends its own.</summary>
    public IReadOnlyList<PeerProgression> GetProgressionReports() => [];

    private void HandleProgressionSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadProgressionSnapshot(span) is { } progression)
        {
            lock (_gate)
            {
                _gold = progression.Gold;
                _experience = progression.Experience;
                _hasProgression = true;
            }
        }
    }
}
