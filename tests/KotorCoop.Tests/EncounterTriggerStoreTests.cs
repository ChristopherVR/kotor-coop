using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The host's side of client-triggered encounters: which requests it acts on, and — more
/// importantly — which it refuses to. The engine call itself cannot be unit-tested, so
/// everything that decides WHETHER to make it lives here.
/// </summary>
public class EncounterTriggerStoreTests
{
    private static EncounterTriggerPacket Report(uint peer, string module, params uint[] ids) =>
        new(peer, module, ids);

    [Fact]
    public void AFreshReportBecomesAFireRequest()
    {
        EncounterTriggerStore store = new();
        store.Report(Report(5, "tar_m03aa", 0x11B), nowMs: 1000);

        Assert.Equal(
            [new EncounterFireRequest(0x11B, 5)],
            store.Collect("tar_m03aa", nowMs: 1000));
    }

    [Fact]
    public void AReportFromAnotherModuleIsIgnored()
    {
        // Object ids restart on a module transition, so a peer standing in tar_m03ab that
        // says "0x11B" is naming a different object than the host's 0x11B. Firing on it
        // would be firing a random encounter.
        EncounterTriggerStore store = new();
        store.Report(Report(5, "tar_m03ab", 0x11B), nowMs: 1000);

        Assert.Empty(store.Collect("tar_m03aa", nowMs: 1000));
    }

    [Fact]
    public void ModuleComparisonIsCaseInsensitive()
    {
        // The engine reports the module name in mixed case depending on where it is read
        // from (the beacon shows END_M01AA, the bridge end_m01aa).
        EncounterTriggerStore store = new();
        store.Report(Report(5, "TAR_M03AA", 0x11B), nowMs: 1000);

        Assert.Single(store.Collect("tar_m03aa", nowMs: 1000));
    }

    [Fact]
    public void AStaleReportStopsCounting()
    {
        // This is how "the client walked out", "the client crashed" and "the datagrams
        // stopped" all arrive: nothing refreshes, so nothing fires. There is no leave event
        // to lose.
        EncounterTriggerStore store = new(freshnessMs: 500);
        store.Report(Report(5, "tar_m03aa", 0x11B), nowMs: 1000);

        Assert.Single(store.Collect("tar_m03aa", nowMs: 1400));
        Assert.Empty(store.Collect("tar_m03aa", nowMs: 1600));
    }

    [Fact]
    public void AnEmptyReportSupersedesTheLastOne()
    {
        EncounterTriggerStore store = new();
        store.Report(Report(5, "tar_m03aa", 0x11B), nowMs: 1000);
        store.Report(Report(5, "tar_m03aa"), nowMs: 1100);

        Assert.Empty(store.Collect("tar_m03aa", nowMs: 1100));
    }

    [Fact]
    public void TwoPlayersInOnePolygonProduceOneRequest()
    {
        // One fight, not two. The dedupe is by encounter; the lowest peer id wins the
        // anchor, which is what makes the request list deterministic.
        EncounterTriggerStore store = new();
        store.Report(Report(9, "tar_m03aa", 0x11B), nowMs: 1000);
        store.Report(Report(4, "tar_m03aa", 0x11B), nowMs: 1000);

        Assert.Equal(
            [new EncounterFireRequest(0x11B, 4)],
            store.Collect("tar_m03aa", nowMs: 1000));
    }

    [Fact]
    public void DifferentEncountersFromDifferentPeersAllSurvive()
    {
        EncounterTriggerStore store = new();
        store.Report(Report(2, "korr_m34aa", 0x60), nowMs: 1000);
        store.Report(Report(3, "korr_m34aa", 0x61, 0x62), nowMs: 1000);

        Assert.Equal(
            [
                new EncounterFireRequest(0x60, 2),
                new EncounterFireRequest(0x61, 3),
                new EncounterFireRequest(0x62, 3),
            ],
            store.Collect("korr_m34aa", nowMs: 1000));
    }

    [Fact]
    public void DuplicatesInsideOneReportCollapse()
    {
        EncounterTriggerStore store = new();
        store.Report(Report(5, "tar_m03aa", 0x11B, 0x11B, 0x11C), nowMs: 1000);

        Assert.Equal(
            [new EncounterFireRequest(0x11B, 5), new EncounterFireRequest(0x11C, 5)],
            store.Collect("tar_m03aa", nowMs: 1000));
    }

    [Fact]
    public void InvalidIdsAreDropped()
    {
        // 0 is "no object" and 0x7F000000 is OBJECT_INVALID; both resolve to nothing and
        // would only ever cost a refused fire.
        EncounterTriggerStore store = new();
        store.Report(Report(5, "tar_m03aa", 0, 0x7F000000, 0x11B), nowMs: 1000);

        Assert.Equal(
            [new EncounterFireRequest(0x11B, 5)],
            store.Collect("tar_m03aa", nowMs: 1000));
    }

    [Fact]
    public void AReportFromPeerZeroIsIgnored()
    {
        // Peer 0 is the host itself and never reports; a datagram claiming it is malformed
        // or spoofed, and the plugin could not find a phantom for it anyway.
        EncounterTriggerStore store = new();
        store.Report(Report(0, "tar_m03aa", 0x11B), nowMs: 1000);

        Assert.Empty(store.Collect("tar_m03aa", nowMs: 1000));
    }

    [Fact]
    public void ForgettingAPeerDropsItsRequestsImmediately()
    {
        // A disconnect must not leave an encounter being fired on behalf of somebody who
        // is no longer in the session, even inside the freshness window.
        EncounterTriggerStore store = new();
        store.Report(Report(5, "tar_m03aa", 0x11B), nowMs: 1000);
        store.Forget(5);

        Assert.Empty(store.Collect("tar_m03aa", nowMs: 1000));
    }

    [Fact]
    public void TheRequestListIsCapped()
    {
        EncounterTriggerStore store = new();
        for (uint peer = 1; peer <= 6; peer++)
        {
            store.Report(
                Report(peer, "m", peer * 10, (peer * 10) + 1, (peer * 10) + 2, (peer * 10) + 3),
                nowMs: 1000);
        }

        Assert.Equal(
            EncounterTriggerStore.MaxRequests,
            store.Collect("m", nowMs: 1000).Count);
    }

    [Fact]
    public void ClearingDropsEverything()
    {
        EncounterTriggerStore store = new();
        store.Report(Report(5, "tar_m03aa", 0x11B), nowMs: 1000);
        store.Clear();

        Assert.Empty(store.Collect("tar_m03aa", nowMs: 1000));
    }
}
