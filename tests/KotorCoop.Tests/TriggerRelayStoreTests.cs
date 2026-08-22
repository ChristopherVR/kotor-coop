using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The host's side of TriggerRelay: which requests it acts on, which it refuses, and — the
/// part <see cref="EncounterTriggerStoreTests"/> never had to cover, because an encounter's
/// engine field gives firing free idempotence — which trigger ids it will never hand out a
/// second request for, even across many ticks of the same client still standing there.
/// </summary>
public class TriggerRelayStoreTests
{
    private static TriggerRelayPacket Report(uint peer, string module, params uint[] ids) =>
        new(peer, module, ids, []);

    [Fact]
    public void AFreshReportBecomesAFireRequest()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);

        Assert.Equal(
            [new TriggerFireRequest(0x50, 5)],
            store.Collect("end_m01aa", nowMs: 1000));
    }

    [Fact]
    public void ARepeatedReportNeverProducesASecondRequest()
    {
        // The whole reason this store exists separately from EncounterTriggerStore: a
        // generic trigger's OnEnter carries no engine one-shot flag, so this bookkeeping IS
        // the idempotence. The client keeps re-sending every tick while it stands in the
        // polygon (a LEVEL, like the encounter channel) and the second, third, and hundredth
        // Collect() for the same trigger id must all come back empty.
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);

        Assert.Equal(
            [new TriggerFireRequest(0x50, 5)],
            store.Collect("end_m01aa", nowMs: 1000));

        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1100);
        Assert.Empty(store.Collect("end_m01aa", nowMs: 1100));

        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 5000);
        Assert.Empty(store.Collect("end_m01aa", nowMs: 5000));
    }

    [Fact]
    public void FiringIsMarkedTheMomentTheRequestIsHandedOut()
    {
        // Optimistic marking, by design (TriggerRelayStore's own header comment): the fired
        // set updates inside the SAME Collect() call that returns the request, not on some
        // later confirmation. A second Collect() a millisecond later — before the engine call
        // could possibly have run — must already see it as fired.
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);

        store.Collect("end_m01aa", nowMs: 1000);

        Assert.Equal([0x50u], store.GetFiredIds("end_m01aa"));
        Assert.Empty(store.Collect("end_m01aa", nowMs: 1000));
    }

    [Fact]
    public void ASecondPeerEnteringAnAlreadyFiredTriggerGetsNothing()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);
        store.Collect("end_m01aa", nowMs: 1000);

        // A different peer, walking into the same polygon later, must not re-fire it.
        store.Report(Report(9, "end_m01aa", 0x50), nowMs: 2000);
        Assert.Empty(store.Collect("end_m01aa", nowMs: 2000));
    }

    [Fact]
    public void TheFiredSetIsPerModule()
    {
        // The same numeric id in a different module names a different trigger (ids reset on
        // a transition), so a fired-set entry from one module must never suppress a request
        // in another.
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);
        store.Collect("end_m01aa", nowMs: 1000);

        store.Report(Report(5, "tar_m02aa", 0x50), nowMs: 1000);
        Assert.Equal(
            [new TriggerFireRequest(0x50, 5)],
            store.Collect("tar_m02aa", nowMs: 1000));
    }

    [Fact]
    public void ForgettingAModuleDropsItsFiredSet()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);
        store.Collect("end_m01aa", nowMs: 1000);
        Assert.Equal([0x50u], store.GetFiredIds("end_m01aa"));

        store.ForgetModule("end_m01aa");
        Assert.Empty(store.GetFiredIds("end_m01aa"));

        // And a later report can fire it again — this module visit is a clean slate.
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 2000);
        Assert.Equal(
            [new TriggerFireRequest(0x50, 5)],
            store.Collect("end_m01aa", nowMs: 2000));
    }

    [Fact]
    public void ModulesWithFiredListsOnlyModulesThatHaveFiredSomething()
    {
        TriggerRelayStore store = new();
        Assert.Empty(store.ModulesWithFired);

        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);
        store.Collect("end_m01aa", nowMs: 1000);

        Assert.Contains("end_m01aa", store.ModulesWithFired);
    }

    [Fact]
    public void AReportFromAnotherModuleIsIgnored()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "tar_m02ab", 0x50), nowMs: 1000);

        Assert.Empty(store.Collect("tar_m02aa", nowMs: 1000));
    }

    [Fact]
    public void ModuleComparisonIsCaseInsensitive()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "END_M01AA", 0x50), nowMs: 1000);

        Assert.Single(store.Collect("end_m01aa", nowMs: 1000));
    }

    [Fact]
    public void AStaleReportStopsCounting()
    {
        TriggerRelayStore store = new(freshnessMs: 500);
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);

        Assert.Single(store.Collect("end_m01aa", nowMs: 1400));
    }

    [Fact]
    public void AnEmptyReportSupersedesTheLastOne()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);
        store.Report(Report(5, "end_m01aa"), nowMs: 1100);

        Assert.Empty(store.Collect("end_m01aa", nowMs: 1100));
    }

    [Fact]
    public void TwoPlayersInOnePolygonProduceOneRequest()
    {
        TriggerRelayStore store = new();
        store.Report(Report(9, "end_m01aa", 0x50), nowMs: 1000);
        store.Report(Report(4, "end_m01aa", 0x50), nowMs: 1000);

        Assert.Equal(
            [new TriggerFireRequest(0x50, 4)],
            store.Collect("end_m01aa", nowMs: 1000));
    }

    [Fact]
    public void DuplicatesInsideOneReportCollapse()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50, 0x50, 0x51), nowMs: 1000);

        Assert.Equal(
            [new TriggerFireRequest(0x50, 5), new TriggerFireRequest(0x51, 5)],
            store.Collect("end_m01aa", nowMs: 1000));
    }

    [Fact]
    public void InvalidIdsAreDropped()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0, 0x7F000000, 0x50), nowMs: 1000);

        Assert.Equal(
            [new TriggerFireRequest(0x50, 5)],
            store.Collect("end_m01aa", nowMs: 1000));
    }

    [Fact]
    public void AReportFromPeerZeroIsIgnored()
    {
        TriggerRelayStore store = new();
        store.Report(Report(0, "end_m01aa", 0x50), nowMs: 1000);

        Assert.Empty(store.Collect("end_m01aa", nowMs: 1000));
    }

    [Fact]
    public void ForgettingAPeerDropsItsRequestsImmediately()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);
        store.Forget(5);

        Assert.Empty(store.Collect("end_m01aa", nowMs: 1000));
    }

    [Fact]
    public void TheRequestListIsCapped()
    {
        TriggerRelayStore store = new();
        for (uint peer = 1; peer <= 6; peer++)
        {
            store.Report(
                Report(peer, "m", peer * 10, (peer * 10) + 1, (peer * 10) + 2, (peer * 10) + 3),
                nowMs: 1000);
        }

        Assert.Equal(
            TriggerRelayStore.MaxRequests,
            store.Collect("m", nowMs: 1000).Count);
    }

    [Fact]
    public void ClearingDropsEverythingIncludingTheFiredSet()
    {
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);
        store.Collect("end_m01aa", nowMs: 1000);
        store.Clear();

        Assert.Empty(store.GetFiredIds("end_m01aa"));
        Assert.Empty(store.ModulesWithFired);

        store.Report(Report(5, "end_m01aa", 0x50), nowMs: 1000);
        Assert.Equal(
            [new TriggerFireRequest(0x50, 5)],
            store.Collect("end_m01aa", nowMs: 1000));
    }

    // ---- native fires (protocol 36 — the reverse relay direction) -----------------------

    [Fact]
    public void TheHostsOwnNativeFireIsRecordedWithNoPacket()
    {
        // CoopHost.ReportNativeFires's own local path — no Report() call at all.
        TriggerRelayStore store = new();
        store.ReportNativeFires(peerId: 1, "end_m01aa", [0x26]);

        Assert.Equal(
            [new TriggerFireRequest(0x26, 1)],
            store.GetNativeFires("end_m01aa"));
    }

    [Fact]
    public void APeersReportedNativeFireIsRecordedWithItsOrigin()
    {
        TriggerRelayStore store = new();
        store.Report(new TriggerRelayPacket(5, "end_m01aa", [], [], [0x26], []), nowMs: 1000);

        Assert.Equal(
            [new TriggerFireRequest(0x26, 5)],
            store.GetNativeFires("end_m01aa"));
    }

    [Fact]
    public void ANativeFireJoinsTheSameFiredSetAWatchDrivenFireUses()
    {
        // The one dedup set: a trigger a peer's own engine already fired natively must never
        // also be handed out as a Collect() request for someone else's watch report.
        TriggerRelayStore store = new();
        store.ReportNativeFires(peerId: 1, "end_m01aa", [0x26]);

        store.Report(Report(5, "end_m01aa", 0x26), nowMs: 1000);
        Assert.Empty(store.Collect("end_m01aa", nowMs: 1000));
        Assert.Equal([0x26u], store.GetFiredIds("end_m01aa"));
    }

    [Fact]
    public void AWatchDrivenFireBlocksALaterNativeFireReportForTheSameId()
    {
        // The reverse of the above: whichever path claims a trigger id first wins, so a
        // native-fire report for an id the relay already fired must not overwrite its origin.
        TriggerRelayStore store = new();
        store.Report(Report(5, "end_m01aa", 0x26), nowMs: 1000);
        store.Collect("end_m01aa", nowMs: 1000);

        store.ReportNativeFires(peerId: 1, "end_m01aa", [0x26]);

        Assert.Empty(store.GetNativeFires("end_m01aa"));
    }

    [Fact]
    public void TwoPeersClaimingTheSameNativeFireKeepTheFirstOrigin()
    {
        TriggerRelayStore store = new();
        store.ReportNativeFires(peerId: 1, "end_m01aa", [0x26]);
        store.ReportNativeFires(peerId: 2, "end_m01aa", [0x26]);

        Assert.Equal(
            [new TriggerFireRequest(0x26, 1)],
            store.GetNativeFires("end_m01aa"));
    }

    [Fact]
    public void NativeFiresArePerModule()
    {
        TriggerRelayStore store = new();
        store.ReportNativeFires(peerId: 1, "end_m01aa", [0x26]);

        Assert.Empty(store.GetNativeFires("tar_m02aa"));
    }

    [Fact]
    public void ForgettingAModuleDropsItsNativeFires()
    {
        TriggerRelayStore store = new();
        store.ReportNativeFires(peerId: 1, "end_m01aa", [0x26]);
        store.ForgetModule("end_m01aa");

        Assert.Empty(store.GetNativeFires("end_m01aa"));

        // Fired-set membership is dropped too, so a re-visit can fire it again.
        store.ReportNativeFires(peerId: 1, "end_m01aa", [0x26]);
        Assert.Equal(
            [new TriggerFireRequest(0x26, 1)],
            store.GetNativeFires("end_m01aa"));
    }

    [Fact]
    public void ClearingDropsNativeFiresToo()
    {
        TriggerRelayStore store = new();
        store.ReportNativeFires(peerId: 1, "end_m01aa", [0x26]);
        store.Clear();

        Assert.Empty(store.GetNativeFires("end_m01aa"));
    }

    [Fact]
    public void AZeroOrObjectInvalidNativeFireIdIsDropped()
    {
        TriggerRelayStore store = new();
        store.ReportNativeFires(peerId: 1, "end_m01aa", [0, 0x7F000000, 0x26]);

        Assert.Equal(
            [new TriggerFireRequest(0x26, 1)],
            store.GetNativeFires("end_m01aa"));
    }

    [Fact]
    public void PeerZeroCannotClaimANativeFire()
    {
        TriggerRelayStore store = new();
        store.ReportNativeFires(peerId: 0, "end_m01aa", [0x26]);

        Assert.Empty(store.GetNativeFires("end_m01aa"));
    }
}
