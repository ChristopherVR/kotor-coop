using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — The mirrored creature-liveness view accumulated from however many pages the
/// host's current publish cycle needed, replaced wholesale on a new cycle rather
/// than merged into the old one.
/// </summary>
public sealed partial class CoopClient
{
    /// <summary>A client never has anything to reconcile against itself; a client's own
    /// publish is dropped, same as <see cref="PublishEnemies"/>.</summary>
    public void PublishCreatureLiveness(string module, IReadOnlyList<CreatureLivenessEntry> entries)
    {
    }

    /// <summary>
    /// The host's latest liveness view for <paramref name="module"/>, accumulated across
    /// however many pages the current publish cycle needed. Same module-gate as
    /// <see cref="GetEnemies"/>: ids from a different module are not stale, they are WRONG
    /// (the same numbers name different creatures there), so a mismatch returns empty rather
    /// than reconciling against a snapshot that means nothing here.
    /// </summary>
    public IReadOnlyList<CreatureLivenessEntry> GetCreatureLiveness(string module) =>
        GetCreatureLivenessView(module).Entries;

    public (IReadOnlyList<CreatureLivenessEntry> Entries, long? CompleteAtMs) GetCreatureLivenessView(string module)
    {
        lock (_gate)
        {
            if (_livenessLocalLoadQuarantined || _hostWorld.IsEmpty || _livenessLoadSerial != _hostWorld.LoadSerial
                || _livenessNativeEpoch != _hostWorld.NativeWorldEpoch
                || !string.Equals(_livenessModule, _hostWorld.Module, StringComparison.OrdinalIgnoreCase)) return ([], null);
            return _livenessModule.Length == 0
                || module.Length == 0
                || string.Equals(_livenessModule, module, StringComparison.OrdinalIgnoreCase)
                    ? ([.. _livenessById.Values], _livenessCompleteAtMs)
                    : ([], null);
        }
    }

    private readonly Dictionary<uint, CreatureLivenessEntry> _livenessById = [];

    private string _livenessModule = string.Empty;

    private uint _livenessGeneration;

    private bool _livenessHasGeneration;
    private uint _livenessLoadSerial;
    private uint _livenessNativeEpoch;
    private bool _livenessLocalLoadQuarantined;
    private bool _livenessAwaitFreshGeneration;

    /// <summary>Pages of the current generation received so far, and when the last one landed
    /// (null until every page of it has).</summary>
    private readonly HashSet<byte> _livenessPages = [];
    private long? _livenessCompleteAtMs;

    public void InvalidateCreatureLivenessForWorldTransition()
    {
        lock (_gate)
        {
            _livenessById.Clear();
            _livenessPages.Clear();
            _livenessCompleteAtMs = null;
            _livenessAwaitFreshGeneration = true;
        }
    }

    public void InvalidateCreatureLivenessForLocalLoad()
    {
        lock (_gate)
        {
            _livenessLocalLoadQuarantined = true;
            _livenessById.Clear();
            _livenessPages.Clear();
            _livenessCompleteAtMs = null;
            _livenessHasGeneration = false;
        }
    }

    private uint _livenessAnomalyCount;

    public void ReportLivenessAnomalyCount(uint count) => _livenessAnomalyCount = count;

    private void HandleCreatureLiveness(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCreatureLiveness(span) is { } liveness) ApplyCreatureLiveness(liveness);
    }

    internal void ApplyCreatureLiveness(CreatureLivenessPacket liveness)
    {
            lock (_gate)
            {
                bool sameNamespace = _livenessHasGeneration; // generation is global within this admitted host session
                if (_livenessAwaitFreshGeneration && sameNamespace && liveness.Generation == _livenessGeneration) return;
                if (liveness.HostNativeEpoch != _hostWorld.NativeWorldEpoch) return;
                if (!string.Equals(liveness.Module, _hostWorld.Module, StringComparison.OrdinalIgnoreCase)
                    || (sameNamespace && _livenessGeneration == liveness.Generation
                        && !string.Equals(_livenessModule, liveness.Module, StringComparison.OrdinalIgnoreCase))) return;
                if (!LivenessAdmissionRule.Accept(liveness.HostSessionId, _hostSessionId,
                    liveness.HostLoadSerial, _hostWorld.LoadSerial, !_hostWorld.IsEmpty,
                    _livenessLocalLoadQuarantined, liveness.Generation, _livenessGeneration, sameNamespace)) return;
                // A NEW cycle — a different module, or the same module's next publish
                // — replaces the accumulated set rather than merging into it: this is
                // a full snapshot every cycle, not a delta, so a body the host has
                // stopped mentioning (its retention window elapsed) must actually
                // disappear from here too, not linger forever. Pages of the SAME
                // cycle accumulate into the same dictionary as they arrive.
                if (!string.Equals(_livenessModule, liveness.Module, StringComparison.OrdinalIgnoreCase)
                    || !_livenessHasGeneration || _livenessGeneration != liveness.Generation)
                {
                    _livenessById.Clear();
                    _livenessPages.Clear();
                    _livenessCompleteAtMs = null;
                    _livenessModule = liveness.Module;
                    _livenessGeneration = liveness.Generation;
                    _livenessHasGeneration = true;
                    _livenessLoadSerial = liveness.HostLoadSerial;
                    _livenessNativeEpoch = liveness.HostNativeEpoch;
                }

                foreach (CreatureLivenessEntry entry in liveness.Entries)
                {
                    _livenessById[entry.ObjectId] = entry;
                }

                _livenessPages.Add(liveness.PageIndex);
                if (_livenessCompleteAtMs is null && _livenessPages.Count >= Math.Max(1, (int)liveness.PageCount))
                {
                    _livenessCompleteAtMs = Environment.TickCount64;
                }

                _livenessAwaitFreshGeneration = false;
            }
    }
}
