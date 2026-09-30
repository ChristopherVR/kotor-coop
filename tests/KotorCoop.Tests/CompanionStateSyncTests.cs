using KotorCoop.Memory;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The two companion-state gaps roadmap items 11 and 12 close (docs/research/
/// companion-state-sync.md): per-companion experience (a slot-keyed sibling of
/// <see cref="ProgressionPumpTests"/>'s PC-experience coverage) and per-companion equipment
/// (riding <see cref="PartyMemberState"/>'s own equip fields). Both flags default OFF.
/// </summary>
public sealed class CompanionStateSyncTests
{
    // ---- codec pinning -----------------------------------------------------------------

    [Fact]
    public void CompanionXpSnapshotRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        uint[] xp = new uint[ProtocolConstants.PartyRosterSlots];
        xp[0] = 3155;
        xp[8] = 42;
        CompanionXpSnapshotPacket original = new(999L, 0b1_0000_0001, xp);

        int length = PacketCodec.Write(buffer, original);
        CompanionXpSnapshotPacket? decoded = PacketCodec.ReadCompanionXpSnapshot(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(999L, decoded.ServerTimeMs);
        Assert.Equal(original.PresentMask, decoded.PresentMask);
        Assert.Equal(original.Xp, decoded.Xp);
    }

    [Fact]
    public void CompanionXpReportRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        uint[] xp = new uint[ProtocolConstants.PartyRosterSlots];
        xp[3] = 777;
        CompanionXpReportPacket original = new(PeerId: 2, PresentMask: 0b1000, xp);

        int length = PacketCodec.Write(buffer, original);
        CompanionXpReportPacket? decoded = PacketCodec.ReadCompanionXpReport(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(2u, decoded.PeerId);
        Assert.Equal(original.PresentMask, decoded.PresentMask);
        Assert.Equal(original.Xp, decoded.Xp);
    }

    [Fact]
    public void ATruncatedCompanionXpSnapshotIsRejected()
    {
        // A half-read array would apply garbage XP straight into a companion's stats block.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        uint[] xp = new uint[ProtocolConstants.PartyRosterSlots];
        int length = PacketCodec.Write(buffer, new CompanionXpSnapshotPacket(1, 0b1, xp));

        Assert.Null(PacketCodec.ReadCompanionXpSnapshot(buffer[..(length - 1)]));
    }

    [Fact]
    public void ATruncatedCompanionXpReportIsRejected()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        uint[] xp = new uint[ProtocolConstants.PartyRosterSlots];
        int length = PacketCodec.Write(buffer, new CompanionXpReportPacket(2, 0b1, xp));

        Assert.Null(PacketCodec.ReadCompanionXpReport(buffer[..(length - 1)]));
    }

    [Fact]
    public void PartySnapshotCarriesCompanionEquipmentAlongsidePosition()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PartySnapshotPacket original = new(999L,
        [
            new PartyMemberState(
                0, new Vector3f(10f, 20f, -1f), 1.5f, 0x2712, 25, 4, 0x271B,
                EquippedBody: "g_a_clothes01", EquippedRightWeapon: "g_w_dblsbr01",
                EquippedLeftWeapon: ""),
            new PartyMemberState(3, new Vector3f(-4f, 8f, 0f), -2.1f, 0x2714, 12),
        ]);

        int length = PacketCodec.Write(buffer, original);
        PartySnapshotPacket? decoded = PacketCodec.ReadPartySnapshot(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(original.Members, decoded.Members);
        Assert.Equal("g_a_clothes01", decoded.Members[0].EquippedBody);
        Assert.Equal("g_w_dblsbr01", decoded.Members[0].EquippedRightWeapon);
        Assert.Equal(string.Empty, decoded.Members[0].EquippedLeftWeapon);

        // The second member never set equipment: it must decode as empty, not as a stale
        // copy of the first member's gear.
        Assert.Equal(string.Empty, decoded.Members[1].EquippedBody);
    }

    /// <summary>
    /// Root cause 1 of docs/research/companion-state-sync.md's 2026-08-28 round: an exactly
    /// 16-character resref (KOTOR's own ResRef limit, no terminator required) used to be
    /// silently truncated to 15 characters crossing the shared-block boundary
    /// (<c>ProcessMemory.TryWriteAsciiString</c> reserved a byte for a terminator no field
    /// actually needs — see that method's own remarks). The WIRE codec exercised here was
    /// never the truncating half — <see cref="PacketWriter.WriteString"/> is length-prefixed,
    /// not fixed-width, so a 16-byte cap already round-trips a 16-character value correctly —
    /// but this pins that fact explicitly, at all three sizes the task named, rather than
    /// leaving it merely implied by the fix living elsewhere.
    /// </summary>
    [Theory]
    [InlineData("g")] // 1 char
    [InlineData("g_w_blstrpstl00")] // 15 chars
    [InlineData("g_w_blstrpstl001")] // 16 chars — KOTOR's own ResRef limit, the live-observed case
    public void PartySnapshotCompanionEquipmentRoundTripsAtEveryResrefBoundaryLength(string resref)
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PartySnapshotPacket original = new(1L,
        [
            new PartyMemberState(
                0, Vector3f.Zero, 0f, 0, 20,
                EquippedBody: resref, EquippedRightWeapon: resref, EquippedLeftWeapon: resref),
        ]);

        int length = PacketCodec.Write(buffer, original);
        PartySnapshotPacket? decoded = PacketCodec.ReadPartySnapshot(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(resref, decoded.Members[0].EquippedBody);
        Assert.Equal(resref, decoded.Members[0].EquippedRightWeapon);
        Assert.Equal(resref, decoded.Members[0].EquippedLeftWeapon);
    }

    // ---- pure rule: max-merge (roadmap item 11) -----------------------------------------

    [Fact]
    public void MergeIntoAdoptsASlotPresentOnlyInTheCandidate()
    {
        uint[] merged = new uint[ProtocolConstants.PartyRosterSlots];
        ushort mergedMask = 0;

        uint[] candidate = new uint[ProtocolConstants.PartyRosterSlots];
        candidate[2] = 1200;

        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, candidateMask: 0b100, candidate);

        Assert.Equal(1200u, merged[2]);
        Assert.Equal(0b100, mergedMask);
    }

    [Fact]
    public void MergeIntoTakesTheHigherValueWhenBothSidesHaveASlot()
    {
        uint[] merged = new uint[ProtocolConstants.PartyRosterSlots];
        ushort mergedMask = 0;

        uint[] first = new uint[ProtocolConstants.PartyRosterSlots];
        first[0] = 500;
        uint[] second = new uint[ProtocolConstants.PartyRosterSlots];
        second[0] = 300;

        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, 0b1, first);
        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, 0b1, second);

        // Never pulled backwards by the lower of the two readings.
        Assert.Equal(500u, merged[0]);

        uint[] third = new uint[ProtocolConstants.PartyRosterSlots];
        third[0] = 900;
        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, 0b1, third);
        Assert.Equal(900u, merged[0]);
    }

    [Fact]
    public void MergeIntoNeverInventsASlotAbsentFromTheCandidate()
    {
        uint[] merged = new uint[ProtocolConstants.PartyRosterSlots];
        ushort mergedMask = 0;

        uint[] candidate = new uint[ProtocolConstants.PartyRosterSlots];
        candidate[5] = 999; // present in the array, but NOT in the mask

        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, candidateMask: 0, candidate);

        Assert.Equal(0, mergedMask);
        Assert.Equal(0u, merged[5]);
    }

    [Fact]
    public void MergeIntoKeepsEachSlotIndependent()
    {
        uint[] merged = new uint[ProtocolConstants.PartyRosterSlots];
        ushort mergedMask = 0;

        uint[] host = new uint[ProtocolConstants.PartyRosterSlots];
        host[0] = 100;
        host[8] = 4000;

        uint[] client = new uint[ProtocolConstants.PartyRosterSlots];
        client[0] = 250;
        client[3] = 60;

        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, 0b1_0000_0001, host);
        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, 0b0000_1001, client);

        Assert.Equal(250u, merged[0]); // higher of 100/250
        Assert.Equal(60u, merged[3]);  // client-only
        Assert.Equal(4000u, merged[8]); // host-only
        Assert.Equal(0b1_0000_1001, mergedMask);
    }

    // ---- pure rule: equip diff detection (roadmap item 12) ------------------------------

    [Fact]
    public void EquipApplyDecisionSkipsAnEmptyDesiredValue()
    {
        // length 0 = the sender has nothing to say for this slot (gear sync off, an
        // unreadable slot) — never a strip of whatever the body already wears.
        Assert.False(EquipApplyDecision.ShouldApply(length: 0, desiredHash: 12345, lastAppliedHash: 0));
    }

    [Fact]
    public void EquipApplyDecisionSkipsAnUnchangedValue()
    {
        Assert.False(EquipApplyDecision.ShouldApply(length: 10, desiredHash: 555, lastAppliedHash: 555));
    }

    [Fact]
    public void EquipApplyDecisionAppliesAGenuinelyNewValue()
    {
        Assert.True(EquipApplyDecision.ShouldApply(length: 10, desiredHash: 555, lastAppliedHash: 111));
    }

    [Fact]
    public void EquipApplyDecisionAppliesTheFirstEverValueForASlot()
    {
        // lastAppliedHash 0 is what a never-yet-applied slot reads as; a genuinely-hashed
        // resref colliding with 0 is astronomically unlikely (FNV-1a over 16 bytes) and not
        // this rule's problem to solve, but the common "first sight" case must still apply.
        Assert.True(EquipApplyDecision.ShouldApply(length: 10, desiredHash: 999, lastAppliedHash: 0));
    }

    // ---- pure rule: NWScript slot id -> CSWSCreature::EquipItem's internal slot index -----

    [Fact]
    public void EquipSlotMapMapsBodyToTheVerifiedBitFlag()
    {
        // 1 << 1 = 2. VERIFIED by full disassembly of CSWInventory's slot resolver (0x5A49C0)
        // and cross-checked live-in-the-binary: CSWSCreature::ForceEquipClothing's own call
        // site (0x00501D69) pushes slot=2 for a body/clothing equip. Body's value happens to
        // equal the old (wrong) "byte offset / 4" formula's result too — coincidence, not
        // corroboration.
        Assert.Equal(EquipSlotMap.InternalSlotBody, EquipSlotMap.ToInternalSlot(nwscriptSlot: 1));
        Assert.Equal(2, EquipSlotMap.ToInternalSlot(1));
        Assert.Equal(0x08, EquipSlotMap.ToSlotByteOffset(1));
    }

    [Fact]
    public void EquipSlotMapMapsRightWeaponToTheVerifiedBitFlag()
    {
        // 1 << 4 = 0x10 (16) — NOT 5 (the old, wrong "+0x14 / 4" formula). VERIFIED two
        // independent ways by disassembly this round: CSWInventory's slot resolver (0x5A49C0)
        // maps bit-flag 0x10 to creature->inventory+0x14, and
        // ExecuteCommandActionEquipItem (0x005355F0) converts NWScript slot 4 to 2^4=16 via an
        // explicit FPU pow() call before ever reaching AddEquipItemActions.
        Assert.Equal(EquipSlotMap.InternalSlotRightWeapon, EquipSlotMap.ToInternalSlot(nwscriptSlot: 4));
        Assert.Equal(0x10, EquipSlotMap.ToInternalSlot(4));
        Assert.Equal(0x14, EquipSlotMap.ToSlotByteOffset(4));
    }

    [Fact]
    public void EquipSlotMapMapsLeftWeaponToTheVerifiedBitFlag()
    {
        // 1 << 5 = 0x20 (32) — NOT 6 (the old, wrong "+0x18 / 4" formula). Same two-way
        // verification as the right-weapon slot: CSWInventory's slot resolver maps bit-flag
        // 0x20 to creature->inventory+0x18, and the 2^slot conversion gives 2^5=32 for
        // NWScript slot 5.
        Assert.Equal(EquipSlotMap.InternalSlotLeftWeapon, EquipSlotMap.ToInternalSlot(nwscriptSlot: 5));
        Assert.Equal(0x20, EquipSlotMap.ToInternalSlot(5));
        Assert.Equal(0x18, EquipSlotMap.ToSlotByteOffset(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(999)]
    public void EquipSlotMapRefusesEveryOtherNwscriptSlot(int nwscriptSlot)
    {
        // Only the three visible slots this project's equipment channels ever ask for (1, 4,
        // 5) are known — anything else must refuse rather than guess an internal index and
        // silently equip into the wrong slot.
        Assert.Equal(-1, EquipSlotMap.ToInternalSlot(nwscriptSlot));
        Assert.Equal(-1, EquipSlotMap.ToSlotByteOffset(nwscriptSlot));
    }

    // ---- PartyPump: settings/capability gating and anti-echo -----------------------------

    private const string Module = "end_m01aa";

    private sealed class CompanionBridge : IGameBridge
    {
        public bool TryReadLivenessEpoch(out uint epoch) { epoch = 1; return true; }
        public bool TryReadLocalPartyRoster(out PartyRoster roster)
        { roster = new PartyRoster(LocalPresentMask, LocalPresentMask, HostNativeEpoch: 1); return true; }
        public bool SupportsPartySync => true;

        public bool SupportsCompanionProgressionSync => true;

        /// <summary>What ReadPartyMembers was called with, last — proves the host branch asks
        /// for equipment only when the caller opted in.</summary>
        public bool? LastReadIncludedEquipment { get; private set; }

        public List<PartyMemberState> PublishedMembers { get; set; } = [];

        /// <summary>What TryWritePartyMembers was called with, last.</summary>
        public bool? LastApplyEquipment { get; private set; }

        public uint[] LocalXp { get; set; } = new uint[ProtocolConstants.PartyRosterSlots];

        public ushort LocalPresentMask { get; set; }

        public (uint[] Xp, ushort Mask)? LastWrittenCompanionXp { get; private set; }

        public string Description => "companion-state-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<PartyMemberState> ReadPartyMembers(bool includeEquipment = false)
        {
            LastReadIncludedEquipment = includeEquipment;
            return PublishedMembers;
        }

        public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members) =>
            TryWritePartyMembers(members, applyEquipment: false);

        public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members, bool applyEquipment)
        {
            LastApplyEquipment = applyEquipment;
            return members.Count;
        }

        public bool TryReadCompanionExperience(uint[] xpBySlot, out ushort presentMask)
        {
            Array.Copy(LocalXp, xpBySlot, Math.Min(LocalXp.Length, xpBySlot.Length));
            presentMask = LocalPresentMask;
            return true;
        }

        public bool TryWriteCompanionExperience(IReadOnlyList<uint> xpBySlot, ushort presentMask)
        {
            LastWrittenCompanionXp = ([.. xpBySlot], presentMask);

            // Write-through, like the real party table: the next TryReadCompanionExperience
            // sees what was just written, exactly as the live engine would. Several tests rely
            // on this to prove a later, lower report cannot pull an already-merged value back
            // down.
            Array.Copy(LastWrittenCompanionXp.Value.Xp, LocalXp, Math.Min(LastWrittenCompanionXp.Value.Xp.Length, LocalXp.Length));
            LocalPresentMask = presentMask;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubPartySession : ICoopSession
    {
        public bool TryGetPartyRoster(out PartyRoster roster)
        { roster = new PartyRoster(0, 0, HostSessionId: 1, HostNativeEpoch: 1); return true; }
        public bool EnemyAuthority { get; set; }

        public List<PartyMemberState> PublishedParty { get; } = [];

        public List<PartyMemberState> HostParty { get; set; } = [];

        public List<(ushort Mask, uint[] Xp)> PublishedCompanionXp { get; } = [];

        public List<(ushort Mask, uint[] Xp)> ReportedCompanionXp { get; } = [];

        public List<PeerCompanionXp> CompanionXpReports { get; } = [];

        public (ushort Mask, uint[] Xp)? FromHostCompanionXp { get; set; }

        public uint LocalPeerId => EnemyAuthority ? CoopHost.HostPeerId : 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public void PublishParty(IReadOnlyList<PartyMemberState> members) => PublishedParty.AddRange(members);

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => HostParty;

        public void PublishProgression(uint gold, uint experience) { }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void PublishCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot) =>
            PublishedCompanionXp.Add((presentMask, [.. xpBySlot]));

        public void ReportCompanionExperience(ushort presentMask, IReadOnlyList<uint> xpBySlot) =>
            ReportedCompanionXp.Add((presentMask, [.. xpBySlot]));

        public IReadOnlyList<PeerCompanionXp> GetCompanionExperienceReports() => CompanionXpReports;

        public bool TryGetCompanionExperience(out ushort presentMask, out uint[] xpBySlot)
        {
            presentMask = FromHostCompanionXp?.Mask ?? 0;
            xpBySlot = FromHostCompanionXp?.Xp ?? new uint[ProtocolConstants.PartyRosterSlots];
            return FromHostCompanionXp is not null;
        }

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state) { }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(ulong flagId, sbyte value) { }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId) { }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false) { }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void SendChat(string text) { }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) { }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }

    private static (CoopAgent Agent, CompanionBridge Bridge, StubPartySession Session) Build(
        bool host, bool syncCompanionXp = true, bool syncCompanionGear = true)
    {
        CompanionBridge bridge = new();
        StubPartySession session = new() { EnemyAuthority = host };
        CoopAgent agent = new(bridge, session, new AgentSettings
        {
            HeavyPumpIntervalMs = 0,
            SyncCompanionXp = syncCompanionXp,
            SyncCompanionGear = syncCompanionGear,
        });
        return (agent, bridge, session);
    }

    [Fact]
    public void CompanionXpSyncOnByDefault()
    {
        // Live-verified 2026-08-27 (docs/research/companion-state-sync.md §6): a host-side
        // and a client-side script-XP grant each converged to the higher value live.
        Assert.True(new AgentSettings().SyncCompanionXp);
    }

    [Fact]
    public void CompanionGearSyncOnByDefault()
    {
        // Live-verified 2026-08-28 (docs/research/companion-state-sync.md §12): the original
        // "lands on the player" failure (§6) and the later weapon-slot bit-flag encoding bug
        // (§11) are both fixed; all three slots (body, right weapon, left weapon) and
        // mid-session propagation to the client (under 3s) are confirmed live.
        Assert.True(new AgentSettings().SyncCompanionGear);
    }

    [Fact]
    public void CoopConfigDefaultsCompanionXpAndGearOn()
    {
        Assert.True(new CoopConfig().CompanionXpSync);
        Assert.True(new CoopConfig().CompanionGearSync);
    }

    [Fact]
    public void ADisabledCompanionXpSyncNeverReadsOrPublishes()
    {
        (CoopAgent agent, CompanionBridge bridge, StubPartySession session) =
            Build(host: true, syncCompanionXp: false);
        bridge.LocalPresentMask = 0b1;
        bridge.LocalXp[0] = 500;

        agent.PumpOnce();

        Assert.Null(bridge.LastWrittenCompanionXp);
        Assert.Empty(session.PublishedCompanionXp);
    }

    [Fact]
    public void TheHostAppliesItsOwnMergedCompanionXpAndPublishesIt()
    {
        (CoopAgent agent, CompanionBridge bridge, StubPartySession session) = Build(host: true);
        bridge.LocalPresentMask = 0b1;
        bridge.LocalXp[0] = 500;

        uint[] peerXp = new uint[ProtocolConstants.PartyRosterSlots];
        peerXp[0] = 800; // higher than the host's own local reading
        session.CompanionXpReports.Add(new PeerCompanionXp(2, 0b1, peerXp));

        agent.PumpOnce();

        Assert.NotNull(bridge.LastWrittenCompanionXp);
        Assert.Equal(800u, bridge.LastWrittenCompanionXp!.Value.Xp[0]);

        (ushort mask, uint[] xp) = Assert.Single(session.PublishedCompanionXp);
        Assert.Equal(0b1, mask);
        Assert.Equal(800u, xp[0]);
    }

    [Fact]
    public void AClientReportsItsOwnReadingAndMirrorsTheHostsHigherOne()
    {
        (CoopAgent agent, CompanionBridge bridge, StubPartySession session) = Build(host: false);
        bridge.LocalPresentMask = 0b1;
        bridge.LocalXp[0] = 300;

        uint[] hostXp = new uint[ProtocolConstants.PartyRosterSlots];
        hostXp[0] = 900;
        session.FromHostCompanionXp = (0b1, hostXp);

        agent.PumpOnce();

        (ushort reportedMask, uint[] reportedXp) = Assert.Single(session.ReportedCompanionXp);
        Assert.Equal(0b1, reportedMask);
        Assert.Equal(300u, reportedXp[0]);

        Assert.NotNull(bridge.LastWrittenCompanionXp);
        Assert.Equal(900u, bridge.LastWrittenCompanionXp!.Value.Xp[0]);
    }

    [Fact]
    public void ThreePeersConvergeToTheHighestCompanionXp()
    {
        // Same 3+ peer shape as ProgressionPumpTests.ThreePeersConvergeToTheHighestExperience,
        // one layer down: every peer's per-slot reading is folded in by
        // CompanionExperienceMerge.MergeInto in one pass, so the party lands on the single
        // highest reading for that companion regardless of report order.
        (CoopAgent agent, CompanionBridge bridge, StubPartySession session) = Build(host: true);
        bridge.LocalPresentMask = 0b1;
        bridge.LocalXp[0] = 100;

        uint[] xpA = new uint[ProtocolConstants.PartyRosterSlots];
        xpA[0] = 500;
        uint[] xpB = new uint[ProtocolConstants.PartyRosterSlots];
        xpB[0] = 1200;
        uint[] xpC = new uint[ProtocolConstants.PartyRosterSlots];
        xpC[0] = 800;
        session.CompanionXpReports.Add(new PeerCompanionXp(2, 0b1, xpA));
        session.CompanionXpReports.Add(new PeerCompanionXp(3, 0b1, xpB));
        session.CompanionXpReports.Add(new PeerCompanionXp(4, 0b1, xpC));

        agent.PumpOnce();

        Assert.Equal(1200u, bridge.LastWrittenCompanionXp!.Value.Xp[0]);
        (ushort mask, uint[] xp) = Assert.Single(session.PublishedCompanionXp);
        Assert.Equal(0b1, mask);
        Assert.Equal(1200u, xp[0]);
    }

    [Fact]
    public void AStaleLowerCompanionXpReportNeverPullsTheHostsMergedValueBackward()
    {
        // The rejoin case, companion side: a peer's report was once the highest the party had
        // seen for this slot, then reports again with a LOWER reading. The host already wrote
        // the earlier merge into its own companion (write-through, see CompanionBridge above),
        // so the next merge starts from that elevated local reading and cannot regress even
        // though the fresh report is lower.
        (CoopAgent agent, CompanionBridge bridge, StubPartySession session) = Build(host: true);
        bridge.LocalPresentMask = 0b1;
        bridge.LocalXp[0] = 200;

        uint[] peerXp = new uint[ProtocolConstants.PartyRosterSlots];
        peerXp[0] = 900;
        session.CompanionXpReports.Add(new PeerCompanionXp(2, 0b1, peerXp));

        agent.PumpOnce();
        Assert.Equal(900u, bridge.LocalXp[0]);

        uint[] staleLowerXp = new uint[ProtocolConstants.PartyRosterSlots];
        staleLowerXp[0] = 100;
        session.CompanionXpReports[0] = new PeerCompanionXp(2, 0b1, staleLowerXp);
        agent.PumpOnce();

        Assert.Equal(900u, bridge.LastWrittenCompanionXp!.Value.Xp[0]);
    }

    [Fact]
    public void CompanionXpNeedsBothTheProgressionAndPartyCapabilities()
    {
        (CoopAgent agent, CompanionBridge bridge, StubPartySession session) = Build(host: true);
        bridge.LocalPresentMask = 0b1;
        bridge.LocalXp[0] = 500;

        // The slot-keyed companion channel needs BOTH capabilities: Progression (a
        // content-independent scalar channel) alone is not enough, unlike the PC's own single
        // scalar XP — the roster keying needs Party's same-save guarantee too.
        session.Capabilities = SessionCapabilities.Progression;
        agent.PumpOnce();
        Assert.Empty(session.PublishedCompanionXp);

        session.Capabilities = SessionCapabilities.Party;
        agent.PumpOnce();
        Assert.Empty(session.PublishedCompanionXp);

        session.Capabilities = SessionCapabilities.Progression | SessionCapabilities.Party;
        agent.PumpOnce();
        Assert.Single(session.PublishedCompanionXp);
    }

    [Fact]
    public void CompanionGearSyncGatesWhetherTheHostReadsEquipmentAtAll()
    {
        (CoopAgent onAgent, CompanionBridge onBridge, _) = Build(host: true, syncCompanionGear: true);
        onAgent.PumpOnce();
        Assert.Equal(true, onBridge.LastReadIncludedEquipment);

        (CoopAgent offAgent, CompanionBridge offBridge, _) = Build(host: true, syncCompanionGear: false);
        offAgent.PumpOnce();
        Assert.Equal(false, offBridge.LastReadIncludedEquipment);
    }

    [Fact]
    public void CompanionGearSyncGatesWhetherAClientAppliesEquipmentAtAll()
    {
        (CoopAgent onAgent, CompanionBridge onBridge, _) = Build(host: false, syncCompanionGear: true);
        onAgent.PumpOnce();
        Assert.Equal(true, onBridge.LastApplyEquipment);

        (CoopAgent offAgent, CompanionBridge offBridge, _) = Build(host: false, syncCompanionGear: false);
        offAgent.PumpOnce();
        Assert.Equal(false, offBridge.LastApplyEquipment);
    }

    /// <summary>
    /// Anti-echo, structurally: <see cref="Pumps.PartyPump.PumpParty"/>'s client branch never
    /// calls <see cref="IGameBridge.ReadPartyMembers"/> at all — a client mirrors, it does not
    /// read its own companions to publish them — so it can never report its own (possibly
    /// stale) copy of a companion's gear back to the host.
    /// </summary>
    [Fact]
    public void AClientNeverReadsItsOwnCompanionsToPublishThem()
    {
        (CoopAgent agent, CompanionBridge bridge, _) = Build(host: false, syncCompanionGear: true);

        agent.PumpOnce();

        Assert.Null(bridge.LastReadIncludedEquipment);
    }
}
