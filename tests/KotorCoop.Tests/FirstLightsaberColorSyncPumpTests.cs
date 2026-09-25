using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// End-to-end (through a real <see cref="CoopAgent"/> tick) coverage of
/// docs/research/class-and-power-grants.md §11's wiring:
/// <c>KotorCoop.Session.Pumps.FirstLightsaberBuildPump</c> detecting a local build (equipped OR
/// simply possessed — round 2's live round found retail leaves the saber unequipped, so a
/// receiver cannot wait on an equip edge alone), publishing the party-wide flag, and giving this
/// character their own colour once the flag is set — behind
/// <see cref="AgentSettings.FirstSaberColorSync"/>. Also covers round 2's live-caught duplicate
/// (an automatic <c>equip</c> was independently re-detected and re-broadcast by
/// <c>ScriptItemGrantPump</c>): the redesign gives instead of equips, through the SAME
/// suppression-ledger discipline every other automatic give in this codebase already uses.
/// </summary>
public class FirstLightsaberColorSyncPumpTests
{
    private const string Module = "danm13";
    private const string Blue = "g_w_lghtsbr01";   // Guardian (3)
    private const string Yellow = "g_w_lghtsbr04"; // Sentinel (5)
    private const uint LocalPlayerObjectId = 0x7FFFFFFF;

    private sealed class ClassAwareBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, Module, 20, 0);

        public List<int> ClassIds { get; set; } = [];

        public bool IsInDialogue { get; set; }

        /// <summary>What <see cref="ReadPossessedItems"/> answers for the local player sentinel
        /// (pool-inclusive — the possession source the live round found the equip-slot check
        /// alone missed).</summary>
        public List<(string Resref, ItemIdentity Identity)> Possessed { get; set; } = [];

        public string Description => "first-saber-build-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public IReadOnlyList<int> ReadLocalClassIds() => ClassIds;

        public IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPossessedItems(
            IReadOnlySet<uint> possessorIds) =>
            possessorIds.Contains(LocalPlayerObjectId)
                ? [.. Possessed.Select(p => (LocalPlayerObjectId, p.Resref, p.Identity))]
                : [];

        public IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPersonalPossessedItems(
            IReadOnlySet<uint> possessorIds) => ReadPossessedItems(possessorIds);

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public EventFlagStore Flags { get; } = new();

        public void SetFlag(ulong flagId, sbyte value) => Flags.Set(flagId, value, LocalPeerId);

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority => false;

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
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

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public List<(string PartnerTag, IReadOnlyList<MaterialisedLootItem> Items)> PublishedGrants { get; } = [];

        public void PublishDialogueGrant(string partnerTag, IReadOnlyList<MaterialisedLootItem> items) =>
            PublishedGrants.Add((partnerTag, items));

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false) { }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void PublishProgression(uint gold, uint experience) { }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void SendChat(string text) { }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, ClassAwareBridge Bridge, StubSession Session) Build(bool firstSaberColorSync)
    {
        ClassAwareBridge bridge = new();
        StubSession session = new();

        CoopAgent agent = new(
            bridge, session,
            new AgentSettings
            {
                CombatPumpIntervalMs = 0,
                FirstSaberColorSync = firstSaberColorSync,
                ScriptItemGrantSampleIntervalMs = 0,
            });

        return (agent, bridge, session);
    }

    [Fact]
    public void OffByDefaultNeverPublishesOrGivesAnything()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: false);
        bridge.Possessed = [(Blue, new ItemIdentity(1, 0, 0, 0, 0))]; // the builder's own build
        agent.PumpOnce();

        Assert.False(session.Flags.TryGet(FirstLightsaberColorRule.FirstSaberBuiltFlagId, out _));
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }

    [Fact]
    public void ASaberSittingUnequippedInTheBuildersBackpackStillPublishesTheFlag()
    {
        // Round 2's live finding: retail's own construction does NOT auto-equip - it lands in
        // the backpack (which, on a live PC, IS the shared party pool). Detection must not
        // depend on an equip edge that may never come.
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [3]; // Guardian - matches what they built
        bridge.Possessed = [(Blue, new ItemIdentity(1, 0, 0, 0, 0))]; // unequipped

        agent.PumpOnce();

        Assert.True(session.Flags.TryGet(FirstLightsaberColorRule.FirstSaberBuiltFlagId, out sbyte value));
        Assert.NotEqual(0, value);
        // They already have one (in the backpack) - never queue a give for themselves.
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }

    [Fact]
    public void AnEquippedSaberStillPublishesTheFlagToo()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [3];
        bridge.Local = bridge.Local with { EquippedRightWeapon = Blue };

        agent.PumpOnce();

        Assert.True(session.Flags.TryGet(FirstLightsaberColorRule.FirstSaberBuiltFlagId, out sbyte value));
        Assert.NotEqual(0, value);
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }

    [Fact]
    public void AnotherPlayerWithNoSaberYetIsGivenTheirOwnClassColourOnceTheFlagIsSet()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5]; // Sentinel
        // Nothing possessed yet - the flag arrives from a peer's build (simulated directly here,
        // since a real peer relay is exercised by SessionIntegrationTests/EventFlagStore's own
        // suite, not this file).
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));
        Assert.Equal(Yellow, resref);
    }

    [Fact]
    public void APlayerWithNoJediClassYetIsGivenNothingButKeepsWatching()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [0]; // Soldier only, no Jedi class yet
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));

        // A class grant lands later - the very next tick should now give it.
        bridge.ClassIds = [3]; // Guardian
        agent.PumpOnce();
        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));
        Assert.Equal(Blue, resref);
    }

    [Fact]
    public void APlayerWhoAlreadyHasAFirstSaberInTheirBackpackIsNeverGivenASecondOne()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5]; // Sentinel
        bridge.Possessed = [(Yellow, new ItemIdentity(1, 0, 0, 0, 0))]; // built it themselves already
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();

        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }

    /// <summary>
    /// Round 2's live-caught duplicate, reproduced at the mechanism level: the automatic give
    /// this pump queues must be suppressed from <c>ScriptItemGrantPump</c>'s own sweep the SAME
    /// way every other automatic give in this codebase already is
    /// (<c>CoopAgent.NoteLocalItemGiven</c>, called by <c>CoopCommandDispatcher</c> BEFORE
    /// submitting the give-loot cheat — <see cref="ClassAwareBridge.ReadPossessedItems"/> here
    /// stands in for the item actually landing). Without that call, this exact sequence is what
    /// produced round 2's duplicate: the give lands, <c>ScriptItemGrantPump</c> samples the new
    /// row as if a script had just granted it, and republishes it as a SECOND grant.
    /// </summary>
    [Fact]
    public void TheAutomaticGiveIsSuppressedFromBeingRePublishedAsAScriptGrant()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5];
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        // First pump: seeds ScriptItemGrantPump's baseline (empty) and queues the give.
        agent.PumpOnce();
        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));
        Assert.Equal(Yellow, resref);

        // Mirrors CoopCommandDispatcher.Items.cs's DrainAutomaticFirstSaberBuild: suppress
        // BEFORE the item lands, exactly like the manual giveloot verb and DrainAutomaticLoot
        // both already do.
        agent.NoteLocalItemGiven(LocalPlayerObjectId, resref);

        // The give "lands" - the local inventory now shows it.
        bridge.Possessed = [(resref, new ItemIdentity(1, 0, 0, 0, 0))];

        agent.PumpOnce();

        Assert.Empty(session.PublishedGrants);
    }

    /// <summary>The negative control for the test above: without the suppression call, the SAME
    /// sequence really does look like a fresh script grant to <c>ScriptItemGrantPump</c> - this
    /// is round 2's bug, reproduced, so the fix above is proven to matter and not just present.
    /// </summary>
    [Fact]
    public void WithoutSuppressionTheSameSequenceWouldHaveBeenRepublished()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5];
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();
        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));

        // No NoteLocalItemGiven call this time.
        bridge.Possessed = [(resref, new ItemIdentity(1, 0, 0, 0, 0))];

        agent.PumpOnce();

        Assert.NotEmpty(session.PublishedGrants);
    }

    [Fact]
    public void ARepeatedFlagAfterTheFirstGiveLandedIsNotDoubled()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5];
        session.Flags.Set(FirstLightsaberColorRule.FirstSaberBuiltFlagId, 1, originPeerId: 1);

        agent.PumpOnce();
        Assert.True(agent.TryDequeuePendingFirstSaberBuild(out string resref));
        Assert.Equal(Yellow, resref);

        // Simulate the give having actually landed on this player's own engine (a real rejoin/
        // resync would re-read this from the live inventory).
        bridge.Possessed = [(resref, new ItemIdentity(1, 0, 0, 0, 0))];

        // The flag is already true and stays true (a resync would simply re-affirm it) - a
        // second pump must not queue a second give.
        agent.PumpOnce();
        Assert.False(agent.TryDequeuePendingFirstSaberBuild(out _));
    }
}
