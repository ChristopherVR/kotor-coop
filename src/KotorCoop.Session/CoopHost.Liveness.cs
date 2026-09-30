using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — The host's full creature-liveness view (docs/research/creature-liveness.md),
/// paginated and broadcast on its own multi-second throttle rather than every tick
/// — a cycle can already be several datagrams.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>Host only: publish every creature this instance's engine currently knows
    /// about in <paramref name="module"/> — see docs/research/creature-liveness.md. Just
    /// stores the latest view; <see cref="BroadcastCreatureLiveness"/> is what actually
    /// paginates and sends it, on its own throttle.</summary>
    public void PublishCreatureLiveness(string module, IReadOnlyList<CreatureLivenessEntry> entries)
        => PublishCreatureLiveness(module, entries, _localWorld.LoadSerial);

    public void PublishCreatureLiveness(string module, IReadOnlyList<CreatureLivenessEntry> entries, uint worldLoadSerial)
        => PublishCreatureLiveness(module, entries, worldLoadSerial, _localWorld.NativeWorldEpoch);

    public void PublishCreatureLiveness(string module, IReadOnlyList<CreatureLivenessEntry> entries, uint worldLoadSerial, uint nativeEpoch)
    {
        lock (_gate)
        {
            _livenessModule = module;
            _livenessEntries = entries;
            _livenessLoadSerial = worldLoadSerial;
            _livenessNativeEpoch = nativeEpoch;
        }
    }

    /// <summary>A client mirrors this; the host has nothing to reconcile against itself.</summary>
    public IReadOnlyList<CreatureLivenessEntry> GetCreatureLiveness(string module) => [];

    private IReadOnlyList<CreatureLivenessEntry> _livenessEntries = [];

    private string _livenessModule = string.Empty;
    private uint _livenessLoadSerial;
    private uint _livenessNativeEpoch;

    /// <summary>Bumped once per actual broadcast CYCLE (not per <see cref="PublishCreatureLiveness"/>
    /// call, which happens far more often than the broadcast throttle allows) — a receiver
    /// uses (module, generation) together to tell a later cycle's pages from an earlier
    /// cycle's, so it replaces its accumulated set rather than merging two different
    /// snapshots together.</summary>
    private uint _livenessGeneration;

    /// <summary>Milliseconds between broadcast cycles. A cycle can be several datagrams (one
    /// per <see cref="ProtocolConstants.MaxCreatureLivenessEntries"/> entries), so unlike the
    /// enemy/party snapshots this is not resent every tick — that would multiply the page
    /// count by the tick rate for no benefit, since the whole channel is meant to be
    /// eventually consistent on a scale of seconds, not ticks.</summary>
    private const int LivenessBroadcastIntervalMs = 2000;

    private long _lastLivenessBroadcastMs;

    /// <summary>Set by <see cref="HandleHello"/> on a fresh (re)admission so the very next
    /// tick's cycle goes out immediately rather than waiting out
    /// <see cref="LivenessBroadcastIntervalMs"/> — a returning client's first liveness packet
    /// is a full snapshot, and every cycle already IS a full snapshot, so forcing the next one
    /// early is the whole mechanism.</summary>
    private bool _forceLivenessBroadcast;

    private uint _livenessAnomalyCount;

    /// <summary>The host's own reconcile controller never runs (nothing is ever published TO
    /// the host to reconcile against), so this is always 0 in practice — wired for symmetry
    /// with <see cref="CoopClient"/> rather than for any host-side behaviour.</summary>
    public void ReportLivenessAnomalyCount(uint count) => _livenessAnomalyCount = count;

    /// <summary>
    /// Sends the host's full creature-liveness view, throttled to roughly once every
    /// <see cref="LivenessBroadcastIntervalMs"/> — unlike the enemy/party snapshots this is
    /// NOT resent every tick, because one cycle can already be several datagrams and resending
    /// the whole burst at the tick rate would multiply that for no benefit; the channel is
    /// meant to converge on a scale of seconds, not ticks. A freshly (re)admitted peer
    /// (<see cref="_forceLivenessBroadcast"/>, set in <see cref="HandleHello"/>) forces the
    /// very next cycle out immediately instead of waiting out the interval — see
    /// docs/research/creature-liveness.md.
    /// </summary>
    private void BroadcastCreatureLiveness(long now, List<PeerConnection> recipients)
    {
        IReadOnlyList<CreatureLivenessEntry> entries;
        string module;
        uint loadSerial;
        uint nativeEpoch;
        lock (_gate)
        {
            entries = _livenessEntries;
            module = _livenessModule;
            loadSerial = _livenessLoadSerial;
            nativeEpoch = _livenessNativeEpoch;
            if (!_hasPublishedLocalWorldDigest || loadSerial != _localWorld.LoadSerial || nativeEpoch != _localWorld.NativeWorldEpoch
                || !string.Equals(module, _localWorld.Module, StringComparison.OrdinalIgnoreCase)) return;
        }

        if (recipients.Count == 0 || module.Length == 0)
        {
            return;
        }

        if (!_forceLivenessBroadcast && now - _lastLivenessBroadcastMs < LivenessBroadcastIntervalMs)
        {
            return;
        }

        _forceLivenessBroadcast = false;
        _lastLivenessBroadcastMs = now;
        _livenessGeneration++;

        foreach (CreatureLivenessPacket page in
            CreatureLivenessPaging.Paginate(now, module, _livenessGeneration, entries, _sessionId, loadSerial, nativeEpoch))
        {
            int length = PacketCodec.Write(_sendBuffer, page);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }
}
