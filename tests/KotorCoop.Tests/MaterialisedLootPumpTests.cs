using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The owner-authority side of docs/research/enemy-materialisation.md §6's automatic wire:
/// noticing an enemy died and capturing its real corpse's loot. What matters most here is the
/// distinction a false positive would get wrong — an enemy that merely drops out of the census
/// (out of range, cap-shed) must never be read as a kill, only one the owner's own engine
/// actually reports <c>Dead</c>.
/// </summary>
public class MaterialisedLootPumpTests
{
    private const string Module = "end_m01aa";

    private sealed class LootBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, Module, 20, 0);

        public List<EnemyState> Enemies { get; set; } = [];

        /// <summary>What <see cref="ReadPossessedItems"/> answers, keyed by possessor id.</summary>
        public Dictionary<uint, List<(string Resref, ItemIdentity Identity)>> Possessed { get; } = [];

        /// <summary>Every set of ids this bridge was actually asked about, so a test can assert
        /// ONE sweep covers however many died on a tick rather than one call per creature.</summary>
        public List<IReadOnlySet<uint>> PossessedQueries { get; } = [];

        public string Description => "loot-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsEnemySync => true;

        public bool IsInDialogue => false;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<EnemyState> ReadEnemies(IReadOnlyList<Vector3f> otherPlayers) => Enemies;

        public IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPossessedItems(
            IReadOnlySet<uint> possessorIds)
        {
            PossessedQueries.Add(possessorIds);

            List<(uint, string, ItemIdentity)> result = [];
            foreach (uint id in possessorIds)
            {
                if (!Possessed.TryGetValue(id, out List<(string Resref, ItemIdentity Identity)>? items))
                {
                    continue;
                }

                foreach ((string resref, ItemIdentity identity) in items)
                {
                    result.Add((id, resref, identity));
                }
            }

            return result;
        }

        public void Dispose()
        {
        }
    }

    private sealed class LootSession : ICoopSession
    {
        public List<(uint OwnerEnemyId, IReadOnlyList<MaterialisedLootItem> Items)> Published { get; } = [];

        public uint LocalPeerId => CoopHost.HostPeerId;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority => true;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(ulong flagId, sbyte value)
        {
        }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId)
        {
        }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void PublishMaterialisedLoot(uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items) =>
            Published.Add((ownerEnemyId, items));

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest)
        {
        }

        public void PublishParty(IReadOnlyList<PartyMemberState> members)
        {
        }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false)
        {
        }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void PublishProgression(uint gold, uint experience)
        {
        }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void SendChat(string text)
        {
        }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, LootBridge Bridge, LootSession Session) Build()
    {
        LootBridge bridge = new();
        LootSession session = new();

        // ScriptItemSync = false: this file's PossessedQueries instrumentation asserts the
        // MATERIALISED-loot pump's own sweep is one call per tick, not one per death — since
        // script_item_sync defaulted ON (2026-08-27, docs/research/dialogue-consequences.md),
        // ScriptItemGrantPump independently samples the local player's own possessed items
        // every tick too, which would otherwise show up as a second, unrelated entry in the
        // same shared query list and break that assertion for a reason that has nothing to do
        // with what this file tests.
        CoopAgent agent = new(
            bridge, session, new AgentSettings { CombatPumpIntervalMs = 0, ScriptItemSync = false });
        return (agent, bridge, session);
    }

    private static EnemyState Alive(uint id) => new(id, 10, 10, Dead: false);

    private static EnemyState Dead(uint id) => new(id, 0, 10, Dead: true);

    [Fact]
    public void ADeathPublishesItsRealCorpseLoot()
    {
        (CoopAgent agent, LootBridge bridge, LootSession session) = Build();

        bridge.Enemies = [Alive(0xE)];
        agent.PumpOnce();
        Assert.Empty(session.Published);

        bridge.Possessed[0xE] =
        [
            ("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
        ];
        bridge.Enemies = [Dead(0xE)];
        agent.PumpOnce();

        (uint ownerId, IReadOnlyList<MaterialisedLootItem> items) = Assert.Single(session.Published);
        Assert.Equal(0xEu, ownerId);
        MaterialisedLootItem item = Assert.Single(items);
        Assert.Equal("g_w_blstrpstl001", item.Resref);
    }

    /// <summary>The distinction that actually matters: a creature that simply falls out of the
    /// census (out of range, cap-shed) is not a kill, and must never be read as one.</summary>
    [Fact]
    public void DroppingOutOfTheCensusWithoutADeadReadingIsNotADeath()
    {
        (CoopAgent agent, LootBridge bridge, LootSession session) = Build();

        bridge.Enemies = [Alive(0xE)];
        agent.PumpOnce();

        // 0xE simply vanishes from the census (distance-shed), never reported Dead.
        bridge.Enemies = [];
        agent.PumpOnce();

        Assert.Empty(session.Published);
    }

    [Fact]
    public void ADeathWithNoCapturedItemsPublishesNothing()
    {
        (CoopAgent agent, LootBridge bridge, LootSession session) = Build();

        bridge.Enemies = [Alive(0xE)];
        agent.PumpOnce();

        // No entry in Possessed at all: a genuinely empty-handed corpse.
        bridge.Enemies = [Dead(0xE)];
        agent.PumpOnce();

        Assert.Empty(session.Published);
    }

    /// <summary>Two enemies dying on the same tick must cost one possessed-items sweep, not
    /// two — see the doc on <see cref="IGameBridge.ReadPossessedItems"/> for why a per-death
    /// call would stall the pump on an ambush wave.</summary>
    [Fact]
    public void SeveralDeathsOnOneTickShareOneSweep()
    {
        (CoopAgent agent, LootBridge bridge, LootSession session) = Build();

        bridge.Enemies = [Alive(0xE), Alive(0xF)];
        agent.PumpOnce();

        bridge.Possessed[0xE] = [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        bridge.Possessed[0xF] = [("g_a_class4001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        bridge.Enemies = [Dead(0xE), Dead(0xF)];
        agent.PumpOnce();

        Assert.Equal(2, session.Published.Count);
        // Only the corpse sweeps count: other pumps (the first-saber watch) read the local
        // player's own inventory (0x7FFFFFFF) on the same bridge.
        var corpseSweeps = bridge.PossessedQueries.Where(q => !q.Contains(0x7FFFFFFFu)).ToList();
        Assert.Single(corpseSweeps);
        Assert.Equal(2, corpseSweeps[0].Count);
    }

    /// <summary>Object ids are reassigned per module load, so an id remembered as dead in one
    /// module must not poison the next — and, symmetrically, an id that was never seen this
    /// module must not be treated as a fresh kill just because it now reads dead.</summary>
    [Fact]
    public void AModuleChangeDoesNotTreatEveryDeadIdAsAFreshKill()
    {
        (CoopAgent agent, LootBridge bridge, LootSession session) = Build();

        bridge.Enemies = [Alive(0xE)];
        agent.PumpOnce();
        bridge.Enemies = [Dead(0xE)];
        agent.PumpOnce();
        Assert.Empty(session.Published); // no loot captured, so nothing published yet

        // A new module: the same numeric id now names a different creature, already dead on
        // the very first census — this must seed quietly, not fire a death for it.
        bridge.Local = bridge.Local with { Module = "tar_m03aa" };
        bridge.Possessed[0xE] = [("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        bridge.Enemies = [Dead(0xE)];
        agent.PumpOnce();

        Assert.Empty(session.Published);
    }
}
