using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Encounter triggers and the generic OnEnter trigger relay: a client's engine
/// cannot safely fire its own encounter or object-creating script (it would drift
/// its own object-id counter), so it reports and this host's engine does the spawn.
/// </summary>
public sealed partial class CoopHost
{
    // ---- encounter triggers -------------------------------------------------------
    //
    // Clients cannot fire their own encounters (the spawn would drift their object-id
    // counter), so they report where they are standing and this instance's engine does the
    // spawn. Fresh reports only, and only from peers in the same module — see
    // EncounterTriggerStore for why this is a level rather than an enter event.

    private readonly EncounterTriggerStore _encounterTriggers = new();

    private void HandleEncounterTrigger(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadEncounterTrigger(span) is { } packet)
        {
            _encounterTriggers.Report(packet, SessionClock.NowMs);
        }
    }

    public IReadOnlyList<EncounterFireRequest> GetEncounterFireRequests(string module) =>
        _encounterTriggers.Collect(module, SessionClock.NowMs);

    // ---- generic trigger relay -----------------------------------------------------
    //
    // A generic trigger's OnEnter already fires correctly on whichever engine the real
    // player stands in — the gap is that a client's engine running it never touches this
    // host's object-id counter, so a plot CreateObject/DestroyObject in that script drifts
    // the two instances apart. Reports are fresh-only, module-scoped, and de-duplicated by
    // trigger with the host's own fired-set never re-offering the same id twice — see
    // TriggerRelayStore.

    private readonly TriggerRelayStore _triggerRelay = new();

    private void HandleTriggerRelay(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadTriggerRelay(span) is { } packet)
        {
            _triggerRelay.Report(packet, SessionClock.NowMs);
        }
    }

    public IReadOnlyList<TriggerFireRequest> GetTriggerFireRequests(string module) =>
        _triggerRelay.Collect(module, SessionClock.NowMs);

    public IReadOnlyList<uint> GetFiredTriggerIds(string module) =>
        _triggerRelay.GetFiredIds(module);

    /// <summary>
    /// The host's own real character natively firing a trigger reaches
    /// <see cref="TriggerRelayStore"/> straight from here — no packet, no round trip, exactly
    /// the entry point a client's report reaches over the wire (<see cref="HandleTriggerRelay"/>).
    /// Both peer types call the SAME <see cref="ICoopSession.PublishTriggerEntries"/>-adjacent
    /// method for this (<c>CoopAgent</c> calls it from both branches of its trigger pump), so
    /// the code path is symmetric even though this half of it never touches the network.
    /// </summary>
    public void PublishNativeTriggerFires(string module, IReadOnlyList<uint> triggerIds) =>
        _triggerRelay.ReportNativeFires(HostPeerId, module, triggerIds);

    /// <summary>Every native fire this host currently knows about for <paramref name="module"/>,
    /// itself included — the caller (<c>CoopAgent</c>) filters out entries whose origin is its
    /// own peer id.</summary>
    public IReadOnlyList<TriggerFireRequest> GetNativeTriggerFireRequests(string module) =>
        _triggerRelay.GetNativeFires(module);

    /// <summary>
    /// Announces this host's fired-set — AND its merged native-fire set with origins — for
    /// every module it currently has either for. Unreliable and re-sent every tick, like
    /// <see cref="BroadcastEnemies"/>: a lost datagram costs a client re-reporting an
    /// already-fired trigger, or re-asking for a native fire it already has, for one more tick,
    /// never a stuck state — <see cref="TriggerRelayStore.Collect"/> and
    /// <see cref="TriggerRelayStore.ReportNativeFires"/> both silently no-op a repeat regardless,
    /// so a client's watch not yet caught up is a bandwidth cost, not a correctness one.
    /// </summary>
    private void BroadcastTriggerRelay(List<PeerConnection> recipients)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        foreach (string module in _triggerRelay.ModulesWithFired)
        {
            IReadOnlyList<uint> fired = _triggerRelay.GetFiredIds(module);
            IReadOnlyList<TriggerFireRequest> native = _triggerRelay.GetNativeFires(module);
            if (fired.Count == 0 && native.Count == 0)
            {
                continue;
            }

            uint[] nativeIds = new uint[native.Count];
            uint[] nativeOrigins = new uint[native.Count];
            for (int i = 0; i < native.Count; i++)
            {
                nativeIds[i] = native[i].TriggerId;
                nativeOrigins[i] = native[i].PeerId;
            }

            int length = PacketCodec.Write(
                _sendBuffer, new TriggerRelayPacket(0, module, [], fired, nativeIds, nativeOrigins));
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
