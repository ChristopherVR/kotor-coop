namespace KotorCoop.Session;

/// <summary>
/// The host's per-peer experience merge (docs/research/progression-sync.md, "XP baseline"
/// section, 2026-09-18). Replaces the bare <c>Math.Max(host, report.Experience)</c> fold in
/// <c>Pumps.PartyPump.PumpProgressionHost</c> with the same fold applied to a per-peer OFFSET:
///
/// <list type="number">
/// <item><b>A peer's FIRST report after it joins (or after its ledger generation changes) never
/// raises the host.</b> It only records a baseline — the peer's XP and the host's XP at that
/// moment. docs/review-seamless-2026-09-18.md §2: a veteran co-op character (5,000 XP) joining
/// a host's earlier save (500 XP) used to lift the host to 5,000 on the first tick.</item>
/// <item><b>Only what the peer earns AFTER that baseline is merged</b>, as a candidate of
/// <c>hostBase + (peerNow - peerBase)</c>, folded by max exactly as before. A shared kill grants
/// on both engines independently (the "why no double-count" reasoning in PartyPump's own doc
/// comment), so the candidate tracks the host and lifts it only by what the peer alone was
/// granted — a conversation reward, a stealth award.</item>
/// <item><b>A peer AT OR BELOW the host never lifts it</b> — the pre-existing property of the
/// bare max, kept: such a report only re-baselines, so a fresh character adopting the host's
/// higher value (the client-side <c>Math.Max</c> write) is never read as 5,000 XP of earnings
/// the tick it lands.</item>
/// <item><b>Rejoin.</b> XP never legitimately goes down inside one run, so a peer that reports
/// BELOW its last seen value on <see cref="DecreasesBeforeRebaseline"/> consecutive folds has
/// reloaded a save (a rejoin keeps its peer id AND its agent generation — the
/// same-module-reload lesson in docs/research/session-save.md) and is re-baselined there. A
/// single lower report is a reordered datagram and is ignored, as the gold ledger ignores
/// its own.</item>
/// </list>
///
/// The three-peer rule is unchanged: every peer's candidate is folded by max in one pass,
/// regardless of arrival order, and the merged value is written back to the host's own party
/// table before it is published, so nothing can regress it afterwards.
/// </summary>
public sealed class ExperienceMerge
{
    /// <summary>Consecutive lower reports that read as a reload rather than a reorder.</summary>
    public const int DecreasesBeforeRebaseline = 2;

    private struct PeerBaseline
    {
        public uint Generation;
        public uint PeerBase;
        public uint HostBase;
        public uint LastSeen;
        public int DecreaseStreak;
    }

    private readonly Dictionary<uint, PeerBaseline> _peers = [];

    /// <summary>How many peers currently hold a baseline — diagnostics and tests.</summary>
    public int TrackedPeers => _peers.Count;

    /// <summary>
    /// Folds one peer's report into the running merged value and returns the result — never
    /// below <paramref name="hostExperience"/>, which is the host's own reading with every
    /// earlier peer of this pass already folded in.
    /// </summary>
    public uint Fold(uint hostExperience, uint peerId, uint generation, uint peerExperience)
    {
        if (!_peers.TryGetValue(peerId, out PeerBaseline b) || b.Generation != generation)
        {
            // First sight of this peer's ledger, or it restarted: a baseline, never a lift.
            _peers[peerId] = new PeerBaseline
            {
                Generation = generation,
                PeerBase = peerExperience,
                HostBase = hostExperience,
                LastSeen = peerExperience,
            };
            return hostExperience;
        }

        if (peerExperience < b.LastSeen)
        {
            b.DecreaseStreak++;
            if (b.DecreaseStreak < DecreasesBeforeRebaseline)
            {
                // One lower report is a reordered datagram; the next one supersedes it.
                _peers[peerId] = b;
                return hostExperience;
            }

            // Two in a row: the peer reloaded. Start again from where it now stands.
            _peers[peerId] = Rebaselined(generation, peerExperience, hostExperience);
            return hostExperience;
        }

        b.DecreaseStreak = 0;
        b.LastSeen = peerExperience;

        if (peerExperience <= hostExperience)
        {
            // Behind or level with the host: nothing to carry, and whatever it earns from here
            // is measured against the host's CURRENT value, not a stale baseline.
            _peers[peerId] = Rebaselined(generation, peerExperience, hostExperience);
            return hostExperience;
        }

        _peers[peerId] = b;

        uint earned = peerExperience > b.PeerBase ? peerExperience - b.PeerBase : 0;
        uint candidate = earned > uint.MaxValue - b.HostBase ? uint.MaxValue : b.HostBase + earned;
        return Math.Max(hostExperience, candidate);
    }

    private static PeerBaseline Rebaselined(uint generation, uint peerExperience, uint hostExperience) => new()
    {
        Generation = generation,
        PeerBase = peerExperience,
        HostBase = hostExperience,
        LastSeen = peerExperience,
    };
}
