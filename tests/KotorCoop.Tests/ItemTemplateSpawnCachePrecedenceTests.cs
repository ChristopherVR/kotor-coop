using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins the object-id-to-resref observation cache fed by
/// <see cref="KotorGameBridge.DrainItemTemplateSpawns"/> and ground-item spawn tracking: a
/// learned resref is available only in the world generation where it was observed — see
/// docs/research/item-identity.md §12/§13.
///
/// Seeds the cache directly through <see cref="KotorGameBridge.RememberSpawnedItemResref"/>
/// (internal, granted to this assembly via <c>InternalsVisibleTo</c> — see that method's own
/// remarks) rather than through a real plugin block: nothing here needs a game process, and
/// self-attaching to sweep for the Tag-cache fallback path is deliberately NOT exercised —
/// <c>MemoryBridgeTests</c>' own header warns that repeatedly scanning the TEST HOST's own GC
/// heap through <c>ReadProcessMemory</c> can kill the runtime. These tests exercise the
/// cache rule only; production metadata consumers also require a live object-array match.
/// </summary>
public class ItemTemplateSpawnCachePrecedenceTests : IDisposable
{
    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);
    private readonly KotorGameBridge _bridge;

    public ItemTemplateSpawnCachePrecedenceTests()
    {
        _bridge = new KotorGameBridge(_memory, ownsMemory: false);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _memory.Dispose();
    }

    [Fact]
    public void ASpawnCacheObservationAnswersForItsCurrentWorld()
    {
        _bridge.RememberSpawnedItemResref(0x135, "g_i_progspike01");

        bool found = _bridge.TryGetSpawnedItemResrefForCurrentWorld(0x135, out string resref);

        Assert.True(found);
        Assert.Equal("g_i_progspike01", resref);
    }

    /// <summary>The exact case this round exists to fix: a stock KOTOR 1 quest item whose Tag
    /// (<c>k_computer_spike</c>) diverges from its real blueprint
    /// (<c>g_i_progspike01</c>) — docs/research/playthrough-endar-spire.md step 5's
    /// live-confirmed pair. The Tag is never even consulted once the spawn cache has an
    /// answer, so a downstream consumer (ScriptItemGrantPump, DialoguePump) can never see the
    /// diverging proxy for an item this session actually watched get created.</summary>
    [Fact]
    public void TheSpawnCacheAnswerIsNeverTheDivergingTagProxy()
    {
        _bridge.RememberSpawnedItemResref(0x136, "g_i_progspike01");

        bool found = _bridge.TryGetSpawnedItemResrefForCurrentWorld(0x136, out string resref);

        Assert.True(found);
        Assert.NotEqual("k_computer_spike", resref);
        Assert.Equal("g_i_progspike01", resref);
    }

    [Fact]
    public void DifferentObjectIdsAreKeptSeparate()
    {
        _bridge.RememberSpawnedItemResref(0x201, "g_i_progspike01");
        _bridge.RememberSpawnedItemResref(0x202, "g_w_blstrpstl001");

        Assert.True(_bridge.TryGetSpawnedItemResrefForCurrentWorld(0x201, out string first));
        Assert.True(_bridge.TryGetSpawnedItemResrefForCurrentWorld(0x202, out string second));

        Assert.Equal("g_i_progspike01", first);
        Assert.Equal("g_w_blstrpstl001", second);
    }

    /// <summary>First writer wins for a given id — the same rule the ground-item spawn cache
    /// already established (KotorGameBridge.GroundItems.cs's own remarks). A second, later
    /// fact about an id already known (e.g. the item-template-spawn ring re-reporting the same
    /// entry after a controller replays a slot) must never displace the first.</summary>
    [Fact]
    public void ASecondFactAboutAnAlreadyKnownIdDoesNotOverwriteTheFirst()
    {
        _bridge.RememberSpawnedItemResref(0x300, "g_i_progspike01");
        _bridge.RememberSpawnedItemResref(0x300, "g_w_blstrpstl001");

        Assert.True(_bridge.TryGetSpawnedItemResrefForCurrentWorld(0x300, out string resref));
        Assert.Equal("g_i_progspike01", resref);
    }

    [Fact]
    public void AnObservedAliasCannotNameAReusedIdAfterWorldLoad()
    {
        Assert.True(KotorGameBridge.IsSpawnedItemResrefCurrentWorld(7, 7));
        Assert.False(KotorGameBridge.IsSpawnedItemResrefCurrentWorld(7, 8));
    }

    /// <summary>OBJECT_INVALID and 0 are never real ids — a defensive entry naming either must
    /// be silently ignored rather than poisoning a later legitimate lookup for that
    /// (impossible in practice, but never zero-cost to check) id. Checked through
    /// <see cref="KotorGameBridge.HasSpawnedItemResref"/> rather than
    /// <see cref="KotorGameBridge.TryGetItemResref"/> deliberately: a MISS on the latter falls
    /// through to a real memory sweep, which is exactly the hazard this test class's own
    /// header explains staying away from.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(0x7F000000u)] // KotorGameBridge.ObjectInvalid
    public void ANonRealObjectIdIsNeverRemembered(uint id)
    {
        _bridge.RememberSpawnedItemResref(id, "g_i_progspike01");

        Assert.False(_bridge.HasSpawnedItemResref(id));
    }
}
