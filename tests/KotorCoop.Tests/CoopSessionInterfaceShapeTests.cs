using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ICoopSession"/> is now the composite of sixteen role interfaces
/// (<see cref="IPeerSession"/>, <see cref="IPartySession"/>, <see cref="ITriggerSession"/>,
/// and so on), split out of one 577-line file, mirroring the same split already done for
/// <c>IGameBridge</c> in KotorCoop.Memory. This is the regression the split must never
/// reintroduce: a session or test fake that implements only the genuinely abstract members
/// must still compile against <see cref="ICoopSession"/> as a whole. Compiling is the whole
/// proof here — the ~14 hand-written ICoopSession fakes elsewhere in this project are the
/// live evidence this already holds.
/// </summary>
public class CoopSessionInterfaceShapeTests
{
    /// <summary>The only members <see cref="ICoopSession"/> does not give a default body for.
    /// If this class fails to compile, the split dropped a default implementation somewhere.</summary>
    private sealed class MinimalSession : ICoopSession
    {
        public uint LocalPeerId => 1;

        public bool IsConnected => false;

        public string LocalName => "minimal";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => false;

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(uint flagId, sbyte value)
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
            int nodeIndex = DialogueState.NoNode, uint nodeSerial = 0, bool forced = false)
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

        public void SetJournalEntry(string questTag, int state)
        {
        }

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void MinimalFakeImplementingOnlyTheAbstractMembersCompiles()
    {
        using ICoopSession session = new MinimalSession();

        Assert.Equal(1u, session.LocalPeerId);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public void DefaultBodiedMembersFallBackToTheirDeclaredDefaults()
    {
        using ICoopSession session = new MinimalSession();

        Assert.Empty(session.GetCreatureLiveness("end_m01aa"));
        Assert.Empty(session.GetEnemyDamage("end_m01aa"));
        Assert.Equal((0u, 0u), session.GetIncomingPhantomDamage());
        Assert.Empty(session.DrainPeerEffects());
        Assert.Empty(session.DrainBarks("end_m01aa"));
        Assert.Empty(session.DrainTransitionMovies());
        Assert.Empty(session.DrainTransitionAnnounce());
        Assert.Empty(session.DrainMidModuleMovies());
        Assert.False(session.TryGetPartyRoster(out _));
        Assert.Empty(session.GetProgressionReports());
        Assert.Empty(session.GetInteractionLocks());
        Assert.Empty(session.ContainerLootRemovals);
        Assert.Empty(session.DrainMaterialisedLoot());
        Assert.Empty(session.DrainDialogueGrants());
        Assert.Equal(WorldDigest.Empty, session.RemoteWorldDigest);
        Assert.Null(session.Network);
    }
}
