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
    public IReadOnlyList<CreatureLivenessEntry> GetCreatureLiveness(string module)
    {
        lock (_gate)
        {
            return _livenessModule.Length == 0
                || module.Length == 0
                || string.Equals(_livenessModule, module, StringComparison.OrdinalIgnoreCase)
                    ? [.. _livenessById.Values]
                    : [];
        }
    }

    private readonly Dictionary<uint, CreatureLivenessEntry> _livenessById = [];

    private string _livenessModule = string.Empty;

    private uint _livenessGeneration;

    private bool _livenessHasGeneration;

    private uint _livenessAnomalyCount;

    public void ReportLivenessAnomalyCount(uint count) => _livenessAnomalyCount = count;

    private void HandleCreatureLiveness(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadCreatureLiveness(span) is { } liveness)
        {
            lock (_gate)
            {
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
                    _livenessModule = liveness.Module;
                    _livenessGeneration = liveness.Generation;
                    _livenessHasGeneration = true;
                }

                foreach (CreatureLivenessEntry entry in liveness.Entries)
                {
                    _livenessById[entry.ObjectId] = entry;
                }
            }
        }
    }
}
