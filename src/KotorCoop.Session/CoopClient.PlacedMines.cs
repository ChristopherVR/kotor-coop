using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — this client's side of party-placed floor-mine replication
/// (docs/research/mines.md §14): its placements and removals go to the host as reliable intents;
/// the host's canonical set comes back as a roster level, of which only the newest is kept.
/// </summary>
public sealed partial class CoopClient
{
    private readonly PlacedMineIntentSender _placedMineIntents = new();
    private readonly Queue<string> _placedMineNotes = new();
    private PlacedMineRosterPacket? _placedMineRoster;

    public void PublishPlacedMine(uint captureSerial, uint moduleHash, PlacedMineRecord record)
    {
        if (_sessionPeerId == 0)
        {
            return;
        }

        if (_placedMineIntents.Enqueue(_sessionPeerId, PlacedMineIntentKind.Placed, moduleHash, captureSerial, 0, record) is null)
        {
            Note($"placement #{captureSerial} not reported: too many unacked mine reports");
        }
    }

    public void PublishPlacedMineGone(uint mineSerial)
    {
        if (_sessionPeerId == 0 || mineSerial == 0)
        {
            return;
        }

        _placedMineIntents.Enqueue(_sessionPeerId, PlacedMineIntentKind.Gone, 0, 0, mineSerial, default);
    }

    public PlacedMineRosterPacket? CurrentPlacedMineRoster
    {
        get
        {
            lock (_gate)
            {
                return _placedMineRoster;
            }
        }
    }

    public IReadOnlyList<string> DrainPlacedMineNotes()
    {
        lock (_placedMineNotes)
        {
            if (_placedMineNotes.Count == 0)
            {
                return [];
            }

            List<string> notes = [.. _placedMineNotes];
            _placedMineNotes.Clear();
            return notes;
        }
    }

    private void Note(string text)
    {
        lock (_placedMineNotes)
        {
            if (_placedMineNotes.Count < 64)
            {
                _placedMineNotes.Enqueue(text);
            }
        }
    }

    /// <summary>A (re)Welcome: the host may be a different process with its own generation count.</summary>
    private void ResetPlacedMineRoster()
    {
        lock (_gate)
        {
            _placedMineRoster = null;
        }
    }

    private void FlushPlacedMineIntents(long now)
    {
        foreach (PlacedMineIntentPacket op in _placedMineIntents.CollectDue(now))
        {
            int length = PacketCodec.Write(_sendBuffer, op);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandlePlacedMineIntentAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPlacedMineIntentAck(span) is { } ack)
        {
            _placedMineIntents.Acknowledge(ack.OriginPeerId, ack.OpSequence);
        }
    }

    /// <summary>Keeps the newest roster. An older generation arriving late is dropped; the
    /// receiver's serial fence makes even a stale one harmless, but there is no reason to
    /// apply it.</summary>
    private void HandlePlacedMineRoster(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPlacedMineRoster(span) is not { } roster)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (_placedMineRoster is { } current && roster.Generation < current.Generation)
            {
                return;
            }

            _placedMineRoster = roster;
        }
    }
}
