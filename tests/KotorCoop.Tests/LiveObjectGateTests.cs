using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The census liveness gate, and the engine lookup it asks.
///
/// ⚠ WHAT THESE TESTS ARE FOR. The agent finds creatures by scanning committed memory for
/// the CSWSCreature vtable word, and a scan cannot tell a live object from the intact bytes
/// of a destroyed one — <c>operator delete</c> leaves the vtable and the object id exactly
/// where they were. Measured live on 2026-08-15: a host's scan held the same object id at
/// two different addresses, its enemy census published an id (<c>0x35</c>) that the host's
/// own <c>ResolveServerObject</c> could not resolve, and 200 points of a client's forwarded
/// damage sat unspent for ever against it (docs/research/enemy-presence-sync.md).
///
/// Nothing about that was falsifiable from inside the census, because a ghost reads a
/// perfectly plausible tag, position and hit-point count. Only the engine's own object array
/// knows, so these prove the two halves of asking it: the lookup arithmetic, and the policy
/// that acts on the answer.
/// </summary>
public class LiveObjectGateTests
{
    // ---- the lookup: a transcription of CGameObjectArray::GetGameObject (0x004D8230) ----

    /// <summary>A fake x86 address space: 4-byte words at 4-byte addresses. The structure is
    /// the game's, so the widths are the game's, whatever the test host is.</summary>
    private sealed class FakeMemory
    {
        private readonly Dictionary<nint, uint> _words = [];

        public nint Tree { get; init; }

        public Func<nint, uint?> Read => a => _words.TryGetValue(a, out uint v) ? v : null;

        public void Write(nint address, uint value) => _words[address] = value;

        /// <summary>Puts an entry at the head of a bucket's chain: id, object, next.</summary>
        public void AddEntry(uint bucket, nint entry, uint objectId, uint obj, uint next)
        {
            Write(Tree + (nint)(bucket * 4), (uint)entry);
            Write(entry, objectId);
            Write(entry + 4, obj);
            Write(entry + 8, next);
        }
    }

    private const nint TreeBase = 0x0100_0000;

    [Theory]
    [InlineData(0x47u, 0x2C4)] // door locked byte
    [InlineData(0xC6u, 0x260)] // placeable locked field
    public void WorldFlagCollectorIgnoresFreedLockedBodyAfterLiveBodyUnlocks(uint id, int lockOffset)
    {
        FakeMemory memory = new() { Tree = TreeBase };
        nint current = 0x0300_0000;
        nint freed = 0x0400_0000;
        memory.AddEntry(id & 0xFFF, 0x0200_0000, id, (uint)current, 0);
        // Both scanned bodies retain the same id, but the old world still says locked.
        memory.Write(current + 4, id);
        memory.Write(freed + 4, id);
        memory.Write(current + lockOffset, 0);
        memory.Write(freed + lockOffset, 1);

        // Exercise the production collector's exact gate with the stale candidate last,
        // the order that previously overwrote the shadow with a spurious re-lock.
        Dictionary<uint, uint> observed = [];
        foreach (nint candidate in new[] { current, freed })
        {
            uint scannedId = memory.Read(candidate + 4)!.Value;
            if (EngineGlobals.IsCurrentServerObject(memory.Read, TreeBase, scannedId, candidate))
                observed[scannedId] = memory.Read(candidate + lockOffset)!.Value;
        }

        Assert.Single(observed);
        Assert.Equal(0u, observed[id]);
        Assert.False(EngineGlobals.IsCurrentServerObject(memory.Read, TreeBase, id, freed));
    }

    [Fact]
    public void WorldFlagCollectorDoesNotPublishGhostsWhileObjectArrayIsUnavailable()
    {
        FakeMemory memory = new() { Tree = TreeBase };
        Assert.False(EngineGlobals.IsCurrentServerObject(memory.Read, TreeBase, 0x47, 0x0300_0000));
        Assert.False(EngineGlobals.IsCurrentServerObject(memory.Read, 0, 0x47, 0x0300_0000));
    }

    [Fact]
    public void ResolvesAnIdFromItsOwnBucket()
    {
        FakeMemory memory = new() { Tree = TreeBase };
        memory.AddEntry(bucket: 0x35, entry: 0x0200_0000, objectId: 0x35, obj: 0x1505_B674, next: 0);

        Assert.True(EngineGlobals.TryLookupObject(memory.Read, TreeBase, 0x35, out uint address));
        Assert.Equal(0x1505_B674u, address);
    }

    /// <summary>An id nothing holds is the whole point of the exercise: the answer must be
    /// "no", not a plausible pointer.</summary>
    [Fact]
    public void AnIdTheArrayDoesNotHoldDoesNotResolve()
    {
        FakeMemory memory = new() { Tree = TreeBase };
        memory.AddEntry(bucket: 0x36, entry: 0x0200_0000, objectId: 0x36, obj: 0x1505_B674, next: 0);

        Assert.False(EngineGlobals.TryLookupObject(memory.Read, TreeBase, 0x35, out uint address));
        Assert.Equal(0u, address);
    }

    /// <summary>Ids that share the low twelve bits collide in one bucket, so the chain has to
    /// be walked rather than the head taken.</summary>
    [Fact]
    public void WalksTheChainWhenIdsCollideInABucket()
    {
        FakeMemory memory = new() { Tree = TreeBase };
        memory.Write(0x0200_0000, 0x1035);
        memory.Write(0x0200_0004, 0xAAAA_0000);
        memory.Write(0x0200_0008, 0x0200_0010);
        memory.Write(0x0200_0010, 0x2035);
        memory.Write(0x0200_0014, 0xBBBB_0000);
        memory.Write(0x0200_0018, 0);
        memory.Write(TreeBase + (0x035 * 4), 0x0200_0000);

        Assert.True(EngineGlobals.TryLookupObject(memory.Read, TreeBase, 0x2035, out uint address));
        Assert.Equal(0xBBBB_0000u, address);
    }

    /// <summary>
    /// The high bit selects the SECOND half of the bucket table instead of being masked away
    /// — the engine allocates ordinary objects and characters from two id spaces, and folding
    /// them together would resolve one onto the other.
    /// </summary>
    [Fact]
    public void TheHighBitSelectsTheOtherHalfOfTheTable()
    {
        FakeMemory memory = new() { Tree = TreeBase };
        memory.AddEntry(bucket: 0x35, entry: 0x0200_0000, objectId: 0x35, obj: 0xAAAA_0000, next: 0);
        memory.AddEntry(bucket: 0x1035, entry: 0x0200_0020, objectId: 0x35, obj: 0xBBBB_0000, next: 0);

        Assert.True(EngineGlobals.TryLookupObject(memory.Read, TreeBase, 0x35, out uint low));
        Assert.True(EngineGlobals.TryLookupObject(memory.Read, TreeBase, 0x8000_0035, out uint high));

        Assert.Equal(0xAAAA_0000u, low);
        Assert.Equal(0xBBBB_0000u, high);
    }

    /// <summary>
    /// A chain that points at itself must END the walk. This reads another process's heap
    /// while that process is running: a torn read is ordinary, and a lookup that can spin
    /// would hang the agent's whole pump rather than skip one creature.
    /// </summary>
    [Fact]
    public void ACircularChainTerminates()
    {
        FakeMemory memory = new() { Tree = TreeBase };
        memory.AddEntry(bucket: 0x35, entry: 0x0200_0000, objectId: 0x99, obj: 0xAAAA_0000, next: 0x0200_0000);

        Assert.False(EngineGlobals.TryLookupObject(memory.Read, TreeBase, 0x35, out _));
    }

    /// <summary>An unreadable word is "I do not know", never "not there" — the caller's
    /// fail-safe depends on being able to tell those apart in aggregate.</summary>
    [Fact]
    public void AnUnreadableBucketFailsRatherThanAnswering()
    {
        FakeMemory empty = new() { Tree = TreeBase };

        Assert.False(EngineGlobals.TryLookupObject(empty.Read, TreeBase, 0x35, out _));
    }

    // ---- the policy: what to do with the answer -------------------------------------

    private static (uint Id, nint Address) Key((uint Id, nint Address) candidate) => candidate;

    /// <summary>
    /// The measured defect, in one test: a scan holding a live creature and the intact bytes
    /// of a destroyed one. Both claim an id; only one of them is what the engine will hand a
    /// peer's damage to, so the ghost must not reach the census.
    /// </summary>
    [Fact]
    public void AGhostIsDroppedAndTheLiveObjectIsKept()
    {
        LiveObjectGate gate = new(id => id == 0x36 ? 0x1923_A620 : 0);

        List<(uint, nint)> kept = gate.Confirm(
            [(0x36u, (nint)0x1923_A620), (0x35u, (nint)0x1505_B674)], Key, out LiveObjectVerdict verdict);

        Assert.Equal([(0x36u, (nint)0x1923_A620)], kept);
        Assert.True(verdict.Trusted);
        Assert.Equal(1, verdict.Kept);
        Assert.Equal(1, verdict.Dropped);
    }

    /// <summary>
    /// The duplicate the host dump actually showed: the same id at two addresses. The id
    /// resolving is not enough — the ADDRESS has to be the one the array names, or the census
    /// publishes a dead body's position and health under a living creature's id.
    /// </summary>
    [Fact]
    public void ADuplicateIdKeepsOnlyTheAddressTheArrayNames()
    {
        LiveObjectGate gate = new(id => id == 0x38 ? 0x1923_A620 : 0);

        List<(uint, nint)> kept = gate.Confirm(
            [(0x38u, (nint)0x1505_B674), (0x38u, (nint)0x1923_A620)], Key, out LiveObjectVerdict verdict);

        Assert.Equal([(0x38u, (nint)0x1923_A620)], kept);
        Assert.Equal(1, verdict.Dropped);
    }

    /// <summary>
    /// ⚠ The gate distrusts ITSELF before it distrusts the world. Confirming nothing at all
    /// out of a full scan means a wrong offset, a torn read or an array mid-rebuild far more
    /// often than it means every creature in the module has ceased to exist — and the cost of
    /// believing it is a session with no enemies, no phantom bodies and no player, which is
    /// worse than the ghosts this exists to remove.
    /// </summary>
    [Fact]
    public void ConfirmingNothingPassesEverythingThroughAndSaysSo()
    {
        LiveObjectGate gate = new(_ => 0);

        List<(uint, nint)> kept = gate.Confirm(
            [(0x36u, (nint)0x1923_A620), (0x35u, (nint)0x1505_B674)], Key, out LiveObjectVerdict verdict);

        Assert.Equal(2, kept.Count);
        Assert.False(verdict.Trusted);
        Assert.Equal(0, verdict.Dropped);
    }

    /// <summary>An empty scan is not a distrust case — there is simply nothing to confirm,
    /// which is the ordinary state at the main menu.</summary>
    [Fact]
    public void AnEmptyScanIsTrustedAndStaysEmpty()
    {
        LiveObjectGate gate = new(_ => 0);

        List<(uint Id, nint Address)> kept = gate.Confirm(
            Array.Empty<(uint Id, nint Address)>(), Key, out LiveObjectVerdict verdict);

        Assert.Empty(kept);
        Assert.True(verdict.Trusted);
    }

    // ---- the same rule at the publish point, where only ids exist -------------------

    /// <summary>
    /// The published census carries wire records, not addresses, so the last-mile gate can
    /// only ask "does the array hold this id at all". It must still refuse the ones it does
    /// not: an id that leaves this instance is a promise that a peer can key a forwarded kill
    /// or a damage debt by it.
    /// </summary>
    [Fact]
    public void ThePublishGateRefusesAnIdTheArrayDoesNotHold()
    {
        LiveObjectGate gate = new(id => id == 0x1A ? 0x1923_A620 : 0);

        List<uint> kept = gate.ConfirmIds([0x1Au, 0x35u], id => id, out LiveObjectVerdict verdict);

        Assert.Equal([0x1Au], kept);
        Assert.Equal(1, verdict.Dropped);
        Assert.True(verdict.Trusted);
    }

    /// <summary>The publish gate shares the scan gate's self-distrust — one rule, one place,
    /// so the two cannot drift apart.</summary>
    [Fact]
    public void ThePublishGateAlsoDistrustsItselfWhenNothingResolves()
    {
        LiveObjectGate gate = new(_ => 0);

        List<uint> kept = gate.ConfirmIds([0x1Au, 0x35u], id => id, out LiveObjectVerdict verdict);

        Assert.Equal(2, kept.Count);
        Assert.False(verdict.Trusted);
    }

    // ---- the report: silence must never be ambiguous --------------------------------

    /// <summary>
    /// ⚠ THE CONTRACT THAT COST A LIVE ROUND. The first version of the gate only logged when
    /// it dropped something, so an empty log meant any of "no ghosts", "the gate never ran" or
    /// "the sink was never wired" — three findings that need three different fixes. Every
    /// state must describe itself, so a log line always exists and always says which one it is.
    /// </summary>
    [Fact]
    public void EveryGateStateDescribesItself()
    {
        foreach (CensusGateState state in Enum.GetValues<CensusGateState>())
        {
            CensusGateReport report = new(state, 0x0ABC_0000, 38, 31, 7, [0x35u]);
            string described = report.Describe();

            Assert.False(string.IsNullOrWhiteSpace(described), $"{state} has no description");
            Assert.StartsWith("gate:", described);
        }
    }

    /// <summary>The one state that used to be silent is the one that most resembles the bug —
    /// the census going out ungated — so it says so in as many words.</summary>
    [Fact]
    public void NotCheckingAtAllIsStatedLoudly()
    {
        string described = new CensusGateReport(
            CensusGateState.NoObjectArray, 0, 38, 38, 0, []).Describe();

        Assert.Contains("NOT CHECKING", described);
        Assert.Contains("38", described);
    }

    /// <summary>A clean pass still reports, and names the numbers — "no ghosts" is a finding,
    /// not a reason to say nothing.</summary>
    [Fact]
    public void ACleanPassStillReportsItsNumbers()
    {
        string described = new CensusGateReport(
            CensusGateState.Confirmed, 0x0ABC_0000, 38, 38, 0, []).Describe();

        Assert.Contains("confirmed 38 of 38", described);
        Assert.Contains("no ghosts", described);
    }

    /// <summary>A drop names the ids it dropped: "7 ghosts" and "7 ghosts, one of which was
    /// 0x35" are different findings, and only the second settles an argument about 0x35.</summary>
    [Fact]
    public void ADropNamesTheIdsItDropped()
    {
        string described = new CensusGateReport(
            CensusGateState.Confirmed, 0x0ABC_0000, 38, 31, 7, [0x35u, 0x36u]).Describe();

        Assert.Contains("dropped 7", described);
        Assert.Contains("0x35", described);
        Assert.Contains("..", described);
    }

    /// <summary>
    /// ⚠ EVERY LINE MUST FIT THE CHANNEL IT IS READ THROUGH. The in-game console answer is 96
    /// bytes (`SharedBlock.ConsoleReplyLength`) and truncates with no mark. Measured
    /// 2026-08-15: every diagnostic answer of the previous round was cut off mid-sentence,
    /// losing exactly the numbers it was asked for while keeping the prose explaining them.
    /// A diagnostic that does not fit is not a diagnostic.
    /// </summary>
    [Fact]
    public void EveryGateLineFitsTheConsoleAnswerChannel()
    {
        uint[] manyIds = [0x35u, 0x36u, 0x37u, 0x38u, 0x39u, 0x3Au, 0x3Bu, 0x3Cu];

        foreach (CensusGateState state in Enum.GetValues<CensusGateState>())
        {
            string described = new CensusGateReport(
                state, 0x1FFF_FFFF, 512, 400, 112, manyIds).Describe();

            Assert.True(
                described.Length <= CensusGateReport.MaxLineLength,
                $"{state} renders {described.Length} chars: {described}");
        }
    }

    [Fact]
    public void PossessedItemRowsRequireTheNativeServerBody()
    {
        // Measured GOG layout: the item scan sees CSWItem; the native object array
        // points sixteen bytes later at its embedded CSWSObject.
        nint currentItemBase = (nint)0x196B0BC8;
        nint reusedIdCurrentObject = currentItemBase + 0x10;
        nint staleFreedBodyWithSameOldId = (nint)0x196ADEA0;

        Assert.True(KotorGameBridge.IsLiveServerItem(true, currentItemBase, reusedIdCurrentObject));
        // Both bodies can still carry the same old object ID and item vtable after a load;
        // only the object-array-resolved body is authoritative for that reused ID.
        Assert.False(KotorGameBridge.IsLiveServerItem(true, staleFreedBodyWithSameOldId, reusedIdCurrentObject));
        Assert.False(KotorGameBridge.IsLiveServerItem(true, currentItemBase, currentItemBase));
        Assert.False(KotorGameBridge.IsLiveServerItem(true, currentItemBase, (nint)0x196ADEB0));
        Assert.False(KotorGameBridge.IsLiveServerItem(false, (nint)0x196B0BC8, (nint)0x196B0BD8));
        Assert.False(KotorGameBridge.IsLiveServerItem(true, 0, (nint)0x10));
    }

    [Fact]
    public void ObjectArrayResolutionRejectsAFreedBodyWhenItsIdHasBeenReused()
    {
        const uint itemId = 0x135;
        nint tree = (nint)0x10000;
        nint entry = (nint)0x20000;
        nint liveServerObject = (nint)0x30010;
        nint currentItemBase = liveServerObject - 0x10;
        nint staleItemBase = (nint)0x31000;
        uint bucket = itemId & 0xFFF;
        Dictionary<nint, uint> words = new()
        {
            [tree + (nint)(bucket * 4)] = (uint)entry,
            [entry] = itemId,
            [entry + 0x04] = (uint)liveServerObject,
            [entry + 0x08] = 0,
        };

        bool resolved = EngineGlobals.TryLookupObject(
            address => words.TryGetValue(address, out uint value) ? value : null,
            tree,
            itemId,
            out uint actualObject);

        Assert.True(resolved);
        Assert.Equal((uint)liveServerObject, actualObject);
        Assert.True(KotorGameBridge.IsLiveServerItem(true, currentItemBase, (nint)actualObject));
        // The old body can still have the same vtable and ID in freed heap memory. Its
        // embedded-object address differs from the object-array's current entry.
        Assert.False(KotorGameBridge.IsLiveServerItem(true, staleItemBase, (nint)actualObject));
    }
}
