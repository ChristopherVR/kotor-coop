using System.Reflection;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ScriptWorldRefreshTests
{
    [Fact]
    public void AHostTransitionOrReplacementDuringExtractionCannotCompleteTheRefresh()
    {
        WorldDigest digest = new(1, 2, "end_m01aa", 3, LoadSerial: 10,
            ScriptRefreshRevision: 7, ScriptRefreshSaveIdentity: 42);
        PlayerState host = new(Vector3f.Zero, 0, "END_M01AA", 20, 0);
        long token = ScriptWorldRefreshRule.Token(100, digest);
        Assert.True(ScriptWorldRefreshRule.MatchesCapturedHost(10, token, 100, digest, host));
        Assert.False(ScriptWorldRefreshRule.MatchesCapturedHost(10, token, 100, digest with { LoadSerial = 11 }, host));
        Assert.False(ScriptWorldRefreshRule.MatchesCapturedHost(10, token, 101, digest, host));
        Assert.False(ScriptWorldRefreshRule.MatchesCapturedHost(10, token, 100, digest with { ScriptRefreshRevision = 8 }, host));
        Assert.False(ScriptWorldRefreshRule.MatchesCapturedHost(10, token, 100, digest with { Module = "end_m01ab" }, host));
        Assert.False(ScriptWorldRefreshRule.MatchesCapturedHost(10, token, 100, digest, null));
        // A refused extraction/rejoin must leave the durable signal eligible for retry.
        Assert.True(ScriptWorldRefreshRule.NeedsRefresh(100, digest, 0));
    }

    [Theory]
    [InlineData(PlayerState.FlagAway)]
    [InlineData(PlayerState.FlagInCombat)]
    [InlineData(PlayerState.FlagInDialogue)]
    [InlineData(PlayerState.FlagInMinigame)]
    [InlineData(PlayerState.FlagDowned)]
    [InlineData(PlayerState.FlagPartyWiped)]
    [InlineData(PlayerState.FlagMenuGuard)]
    public void HostBecomingBusyDuringExtractionDefersRatherThanConsumingTheSignal(uint flags)
    {
        WorldDigest digest = new(1, 2, "end_m01aa", 3, LoadSerial: 10,
            ScriptRefreshRevision: 7, ScriptRefreshSaveIdentity: 42);
        long token = ScriptWorldRefreshRule.Token(100, digest);
        PlayerState host = new(Vector3f.Zero, 0, "end_m01aa", 20, 0, Flags: flags);
        Assert.False(ScriptWorldRefreshRule.MatchesCapturedHost(10, token, 100, digest, host));
        Assert.True(ScriptWorldRefreshRule.NeedsRefresh(100, digest, 0));
        Assert.True(ScriptWorldRefreshRule.MatchesCapturedHost(10, token, 100, digest,
            host with { Flags = 0 }));
    }

    [Fact]
    public void DisabledClassSharingDoesNotStrandANonJediCharacterInTheOldWorld()
    {
        Assert.True(ScriptWorldRefreshRule.CanCaptureCharacter(false, [0]));
        Assert.True(ScriptWorldRefreshRule.CanCaptureCharacter(false, []));
        Assert.False(ScriptWorldRefreshRule.CanCaptureCharacter(true, [0]));
        Assert.False(ScriptWorldRefreshRule.CanCaptureCharacter(true, []));
        Assert.True(ScriptWorldRefreshRule.CanCaptureCharacter(true, [0, 3]));
    }

    [Fact]
    public void OnlyARealFirstJftsHostClassEdgeRefreshesEachLoadedWorld()
    {
        var (refresh, state) = ScriptWorldRefreshRule.ObserveHostClass(default, 10, 3, 5, "end_m01aa", true);
        Assert.False(refresh); // existing class when session began is the baseline
        (refresh, state) = ScriptWorldRefreshRule.ObserveHostClass(state, 11, 4, 5, "end_m01aa", true);
        Assert.True(refresh);
        (refresh, state) = ScriptWorldRefreshRule.ObserveHostClass(state, 11, 4, 5, "end_m01aa", true);
        Assert.False(refresh); // retransmitted class-grant packet cannot change local serial
        (refresh, state) = ScriptWorldRefreshRule.ObserveHostClass(state, 12, 5, 5, "end_m01aa", true);
        Assert.False(refresh); // at most one refresh of this opening
        (refresh, state) = ScriptWorldRefreshRule.ObserveHostClass(state, 13, 3, 6, "end_m01aa", true);
        Assert.True(refresh); // genuinely new/load world gets its own refresh
    }

    [Theory]
    [InlineData(false, "end_m01aa", 3)]
    [InlineData(true, "danm13", 3)]
    [InlineData(true, "end_m01aa", 0)]
    public void StockJediChoicesAndOtherModulesDoNotRequestWorldReloads(bool detected, string module, int classId)
    {
        JftsClassRefreshState baseline = new(true, 0, false, 0);
        Assert.False(ScriptWorldRefreshRule.ObserveHostClass(baseline, 1, classId, 0, module, detected).Refresh);
    }

    [Fact]
    public void CompletedRefreshDoesNotLoopAfterReconnectOrClassGrantResync()
    {
        WorldDigest digest = new(1, 2, "end_m01aa", 3, ScriptRefreshRevision: 7, ScriptRefreshSaveIdentity: 42);
        long completed = ScriptWorldRefreshRule.Token(100, digest);
        Assert.False(ScriptWorldRefreshRule.NeedsRefresh(100, digest, completed));
        Assert.True(ScriptWorldRefreshRule.NeedsRefresh(101, digest, completed));
        Assert.True(ScriptWorldRefreshRule.NeedsRefresh(100, digest with { ScriptRefreshRevision = 8 }, completed));
        Assert.False(ScriptWorldRefreshRule.NeedsRefresh(100, digest with { ScriptRefreshSaveIdentity = 0 }, completed));
        Assert.False(ScriptWorldRefreshRule.NeedsRefresh(0, digest, completed));
    }

    [Fact]
    public void RefreshSignalSurvivesOrdinaryDigestUpdatesButNotAHostLoad()
    {
        using CoopHost host = new(new SessionSettings { Port = 0 });
        WorldDigest digest = new(1, 2, "end_m01aa", 3, LoadSerial: 4);
        host.PublishWorldDigest(digest);
        host.PublishScriptWorldRefresh(42);
        host.PublishWorldDigest(digest with { SaveIdentity = 7 });
        WorldDigest published = HostDigest(host);
        Assert.Equal(1u, published.ScriptRefreshRevision);
        Assert.Equal(42u, published.ScriptRefreshSaveIdentity);
        host.PublishWorldDigest(digest with { LoadSerial = 5 });
        Assert.Equal(0u, HostDigest(host).ScriptRefreshRevision);
        Assert.Equal(0u, HostDigest(host).ScriptRefreshSaveIdentity);
    }

    [Fact]
    public void RefreshFieldsRoundTripAndTruncatedPacketsAreRefused()
    {
        WorldDigest digest = new(1, 2, "end_m01aa", 3, 4, 1, 7, 42);
        byte[] bytes = new byte[1024];
        int length = PacketCodec.Write(bytes, new WorldDigestPacket(1, digest));
        Assert.Equal(digest, PacketCodec.ReadWorldDigest(bytes.AsSpan(0, length))!.Digest);
        Assert.Null(PacketCodec.ReadWorldDigest(bytes.AsSpan(0, length - 1)));
    }

    [Fact]
    public void DirectCoopSaveAllocatesItsOwnSlotAndLeavesUserQuicksaveUntouched()
    {
        string game = Path.Combine(Path.GetTempPath(), "kcoop-direct-save-" + Guid.NewGuid());
        string saves = Path.Combine(game, "saves");
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);
        Directory.CreateDirectory(quick);
        Directory.CreateDirectory(Path.Combine(saves, "000123 - personal"));
        File.WriteAllText(Path.Combine(quick, "SAVEGAME.sav"), "personal quicksave");
        try
        {
            using SaveBridge bridge = new();
            using SessionManager manager = new(bridge, new AgentSettings());
            CoopCommandDispatcher dispatcher = new(manager, bridge, 7777, _ => new SessionSettings(), gameDirectory: game);
            dispatcher.Dispatch(ConsoleCommand.Parse("save Co-op"));
            Assert.Equal(CheatKinds.CoopSave, bridge.Request.Kind);
            Assert.Equal(124, bridge.Request.Amount);
            Assert.Equal("Co-op", bridge.Request.Text);
            Assert.Equal("personal quicksave", File.ReadAllText(Path.Combine(quick, "SAVEGAME.sav")));
            Assert.False(Directory.Exists(Path.Combine(saves, CoopSaveRedirect.HoldbackFolder)));
        }
        finally { Directory.Delete(game, true); }
    }

    [Fact]
    public void FreshDedicatedSaveBecomesTheLiveSnapshotWithoutOverridingNewerQuicksave()
    {
        string game = Path.Combine(Path.GetTempPath(), "kcoop-live-save-" + Guid.NewGuid());
        string saves = Path.Combine(game, "saves");
        DateTime now = DateTime.UtcNow;
        string[] folders = ["000001 - AUTOSAVE", "000002 - Co-op", CoopSaveRedirect.QuickSaveFolder];
        try
        {
            foreach (string folder in folders)
            {
                string path = Path.Combine(saves, folder);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "SAVEGAME.sav"), "archive");
                File.SetLastWriteTimeUtc(Path.Combine(path, "SAVEGAME.sav"), now.AddMinutes(-3));
            }
            string coop = Path.Combine(saves, folders[1]);
            File.SetLastWriteTimeUtc(Path.Combine(coop, "SAVEGAME.sav"), now.AddMinutes(-1));
            Assert.Equal(coop, SaveDigest.FindLiveSessionSave(game));
            string quick = Path.Combine(saves, folders[2]);
            File.SetLastWriteTimeUtc(Path.Combine(quick, "SAVEGAME.sav"), now);
            Assert.Equal(quick, SaveDigest.FindLiveSessionSave(game));
        }
        finally { Directory.Delete(game, true); }
    }

    private static WorldDigest HostDigest(CoopHost host) =>
        (WorldDigest)typeof(CoopHost).GetField("_localWorld", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private sealed class SaveBridge : IGameBridge
    {
        public CheatRequest Request;
        public string Description => "script-save-test";
        public bool IsAttached => true;
        public bool CanWriteRemotes => true;
        public bool SupportsCheats => true;
        public bool TryReadLocalState(out PlayerState state) { state = new(Vector3f.Zero, 0, "end_m01aa", 20, 0); return true; }
        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;
        public bool TryRequestCheat(CheatRequest request, out uint serial) { Request = request; serial = 1; return true; }
        public void Dispose() { }
    }
}
