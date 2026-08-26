using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Encounter triggers and the generic OnEnter trigger relay this client reports
/// to the host (it cannot safely fire its own encounter or object-creating script),
/// plus the host's merged fired-set and native-fire broadcast applied here.
/// </summary>
public sealed partial class CoopClient
{
    // ---- encounter triggers -------------------------------------------------------

    private string _encounterModule = string.Empty;
    private uint[] _encounterIds = [];

    /// <summary>
    /// The set the agent last read out of the plugin. Held rather than queued: this is a
    /// level, so the newest set is the only one worth sending and an older one is never
    /// worth retransmitting.
    /// </summary>
    public void PublishEncounterTriggers(string module, IReadOnlyList<uint> encounterIds)
    {
        lock (_gate)
        {
            _encounterModule = module ?? string.Empty;
            _encounterIds = [.. encounterIds.Take(ProtocolConstants.MaxEncounterTriggers)];
        }
    }

    private void SendEncounterTriggers()
    {
        string module;
        uint[] ids;

        lock (_gate)
        {
            if (_encounterIds.Length == 0 || _localPeerId == 0)
            {
                // Nothing to say. Deliberately silent rather than sending an empty set every
                // tick: the host ages a peer's last report out on its own, so "I left" needs
                // no datagram and standing in open ground costs no traffic.
                return;
            }

            module = _encounterModule;
            ids = _encounterIds;
        }

        int length = PacketCodec.Write(
            _sendBuffer, new EncounterTriggerPacket(_localPeerId, module, ids));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    // ---- generic trigger relay -----------------------------------------------------

    private string _triggerModule = string.Empty;
    private uint[] _triggerIds = [];
    private readonly Dictionary<string, uint[]> _firedTriggersByModule =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The set the agent last read out of the plugin's watch. Held rather than
    /// queued, exactly like <see cref="PublishEncounterTriggers"/>: this is a level, so only
    /// the newest set is worth sending.</summary>
    public void PublishTriggerEntries(string module, IReadOnlyList<uint> triggerIds)
    {
        lock (_gate)
        {
            _triggerModule = module ?? string.Empty;
            _triggerIds = [.. triggerIds.Take(ProtocolConstants.MaxTriggerRelayEntries)];
        }
    }

    private string _nativeFireModule = string.Empty;
    private uint[] _nativeFireIds = [];

    /// <summary>The client half of protocol 36's reverse relay: this player's own engine
    /// natively fired these (already allowlist- and eligibility-filtered by the plugin's
    /// <c>TriggerFireHook</c> observer, exactly like <see cref="PublishTriggerEntries"/>'s
    /// watch is). Held as a level for the same reason that set is.</summary>
    public void PublishNativeTriggerFires(string module, IReadOnlyList<uint> triggerIds)
    {
        lock (_gate)
        {
            _nativeFireModule = module ?? string.Empty;
            _nativeFireIds = [.. triggerIds.Take(ProtocolConstants.MaxTriggerNativeFires)];
        }
    }

    private void SendTriggerEntries()
    {
        string module;
        uint[] ids;
        uint[] nativeIds;

        lock (_gate)
        {
            if ((_triggerIds.Length == 0 && _nativeFireIds.Length == 0) || _localPeerId == 0)
            {
                // Nothing to say, same silence PublishEncounterTriggers's sender keeps: the
                // host ages a peer's last report out on its own.
                return;
            }

            // Both fields are published from the SAME plugin frame pass and always name the
            // current module, so one of the two module strings (whichever is non-empty) is
            // authoritative for both — they can only disagree for one tick around a module
            // change, and a stale native-fire id against a fresh module is harmless: the host
            // rejects it the same way any cross-module id already is elsewhere in this channel.
            module = _triggerIds.Length > 0 ? _triggerModule : _nativeFireModule;
            ids = _triggerIds;
            nativeIds = _nativeFireIds;
        }

        int length = PacketCodec.Write(
            _sendBuffer, new TriggerRelayPacket(_localPeerId, module, ids, [], nativeIds, []));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    public IReadOnlyList<uint> GetFiredTriggerIds(string module)
    {
        lock (_gate)
        {
            return _firedTriggersByModule.TryGetValue(module ?? string.Empty, out uint[]? fired)
                ? fired
                : [];
        }
    }

    private readonly Dictionary<string, TriggerFireRequest[]> _nativeFiresByModule =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most recent host broadcast of every peer's native fires for
    /// <paramref name="module"/>, itself included — <c>CoopAgent</c> filters out entries whose
    /// origin is this client's own <see cref="LocalPeerId"/>.</summary>
    public IReadOnlyList<TriggerFireRequest> GetNativeTriggerFireRequests(string module)
    {
        lock (_gate)
        {
            return _nativeFiresByModule.TryGetValue(module ?? string.Empty, out TriggerFireRequest[]? requests)
                ? requests
                : [];
        }
    }

    private void HandleTriggerRelay(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadTriggerRelay(span) is { } triggerRelay && triggerRelay.PeerId == 0)
        {
            lock (_gate)
            {
                // A LEVEL, replaced wholesale per module — the host sends its complete
                // fired-set every tick, so there is no "unfired" edge to miss.
                _firedTriggersByModule[triggerRelay.Module ?? string.Empty] =
                    [.. triggerRelay.FiredIds];

                // Protocol 36: the merged native-fire set, 1:1 matched against its
                // origin peer. A malformed broadcast (mismatched list lengths) is
                // dropped defensively rather than zipped into wrong pairs.
                string module = triggerRelay.Module ?? string.Empty;
                if (triggerRelay.NativeFiredIds.Count > 0
                    && triggerRelay.NativeFiredIds.Count == triggerRelay.NativeFiredOriginPeerIds.Count)
                {
                    TriggerFireRequest[] requests = new TriggerFireRequest[triggerRelay.NativeFiredIds.Count];
                    for (int i = 0; i < requests.Length; i++)
                    {
                        requests[i] = new TriggerFireRequest(
                            triggerRelay.NativeFiredIds[i], triggerRelay.NativeFiredOriginPeerIds[i]);
                    }

                    _nativeFiresByModule[module] = requests;
                }
                else if (triggerRelay.NativeFiredIds.Count == 0)
                {
                    _nativeFiresByModule[module] = [];
                }
            }
        }
    }
}
