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
}
