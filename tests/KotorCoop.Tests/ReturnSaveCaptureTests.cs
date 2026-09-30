using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ReturnSaveCaptureTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "kcoop-return-" + Guid.NewGuid().ToString("N"));
    private readonly SaveBridge _bridge = new();
    private readonly SessionManager _manager;
    private readonly CoopCommandDispatcher _dispatcher;

    public ReturnSaveCaptureTests()
    {
        Directory.CreateDirectory(Path.Combine(_game, "saves"));
        Directory.CreateDirectory(Path.Combine(_game, "saves", "000001 - AUTOSAVE"));
        _manager = new(_bridge, new AgentSettings());
        _manager.Connect(true, null, new SessionSettings { PlayerName = "Tester", Port = 0 });
        _dispatcher = new(_manager, _bridge, 27140, _ => new SessionSettings(), gameDirectory: _game);
    }

    public void Dispose()
    {
        _manager.Dispose();
        Directory.Delete(_game, true);
    }

    [Theory]
    [InlineData(RejoinTrigger.HostReload)]
    [InlineData(RejoinTrigger.SaveMismatch)]
    public void AutomaticAdoptionWaitsForFreshDedicatedCaptureWithoutQuickSave(RejoinTrigger trigger)
    {
        WriteSave(Path.Combine(_game, "saves", "000000 - QUICKSAVE"));
        string quick = Path.Combine(_game, "saves", "000000 - QUICKSAVE", "SAVEGAME.sav");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(quick));
        DateTime stamp = File.GetLastWriteTimeUtc(quick);
        Call("TriggerRejoin", trigger, _manager.Current!, new Action(() => { }), false);
        Assert.Empty(_bridge.Requests);
        Assert.True((bool)Call("MaintainAutomaticRejoinCapture", 1000L)!);
        Assert.Equal(CheatKinds.CoopSave, Assert.Single(_bridge.Requests).Kind);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(quick)));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(quick));
        Assert.Equal(RejoinPhase.Idle, Field<RejoinDecisionState>("_rejoinState").Phase);
        Assert.True((bool)Call("MaintainAutomaticRejoinCapture", 2000L)!);
        Assert.Equal(RejoinPhase.Idle, Field<RejoinDecisionState>("_rejoinState").Phase);
    }

    [Fact]
    public void ActualDownedWipeWithoutLivingCheckpointDoesNotStartImpossibleSaveLoop()
    {
        _bridge.Flags = PlayerState.FlagDowned | PlayerState.FlagPartyWiped;
        Call("TriggerRejoin", RejoinTrigger.WipeRecovery, _manager.Current!, new Action(() => { }), false);
        Assert.Empty(_bridge.Requests);
        Assert.False((bool)Call("MaintainAutomaticRejoinCapture", 1000L)!);
        Assert.Equal(RejoinPhase.Idle, Field<RejoinDecisionState>("_rejoinState").Phase);
    }

    [Fact]
    public void ActualDownedWipeHasUsableVerifiedPreWipeCharacterWithoutSavingDeadActor()
    {
        string checkpoint = Path.Combine(_game, "saves", "000002 - Co-op");
        WriteSave(checkpoint);
        Assert.Equal(CharacterCapture.Written, ClientCharacterPersistence.Capture(
            Path.Combine(_game, "saves"), checkpoint, 1, out _));
        _bridge.Flags = PlayerState.FlagDowned | PlayerState.FlagPartyWiped;
        Assert.True((bool)Call("TryUseAutomaticRecoveryCheckpoint")!);
        Call("TriggerRejoin", RejoinTrigger.WipeRecovery, _manager.Current!, new Action(() => { }), false);
        Assert.Empty(_bridge.Requests);
        Assert.False((bool)Call("MaintainAutomaticRejoinCapture", 1000L)!);
    }

    private object? Call(string name, params object[] args) => typeof(CoopCommandDispatcher)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_dispatcher, args);
    private T Field<T>(string name) => (T)typeof(CoopCommandDispatcher)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_dispatcher)!;
    private void SetField(string name, object value) => typeof(CoopCommandDispatcher)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_dispatcher, value);
    private string Dedicated => Path.Combine(_game, "saves", $"{_bridge.Requests[^1].Amount:D6} - Co-op");
    private void Answer(int status = CheatStatus.Applied)
    {
        _bridge.Answered = (uint)_bridge.Requests.Count;
        _bridge.Status = status;
        Call("ReflectCheatAnswer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticReturnsOnlyRequestDedicatedSlotAndLeaveUserQuickSaveBytesAlone(bool solo)
    {
        string quick = Path.Combine(_game, "saves", NamedSaveRule.QuickSaveFolder);
        WriteSave(quick);
        string file = Path.Combine(quick, "SAVEGAME.sav");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(file));
        DateTime stamp = File.GetLastWriteTimeUtc(file);
        if (solo) Call("BeginSoloReturnCapture", 1000L);
        else Call("BeginHostReturnFreshSave", 1000L, "test");
        CheatRequest request = Assert.Single(_bridge.Requests);
        Assert.Equal(CheatKinds.CoopSave, request.Kind);
        Assert.True(request.Amount >= 2);
        Assert.Equal(CoopSaveSlot.SlotLabel, request.Text);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(file)));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void NewUnrelatedSaveCannotReleaseHostServingButDedicatedSlotCan()
    {
        Call("BeginHostReturnFreshSave", 1000L, "test");
        WriteSave(Path.Combine(_game, "saves", "000999 - unrelated"));
        Answer();
        Call("MaintainHostReturnFreshSave", 1100L);
        Call("MaintainHostReturnFreshSave", 2000L);
        Assert.False(Field<bool>("_hostReturnSaveReady"));
        WriteSave(Dedicated);
        Call("MaintainHostReturnFreshSave", 2100L);
        Assert.False(Field<bool>("_hostReturnSaveReady"));
        Call("MaintainHostReturnFreshSave", 2900L);
        Assert.True(Field<bool>("_hostReturnSaveReady"));
    }

    [Fact]
    public void ExactDedicatedSlotRequiresNativeAnswerAndStableCompletedArchive()
    {
        Call("BeginHostReturnFreshSave", 1000L, "test");
        WriteSave(Dedicated);
        Call("MaintainHostReturnFreshSave", 1100L);
        Call("MaintainHostReturnFreshSave", 2000L);
        Assert.False(Field<bool>("_hostReturnSaveReady"));
        Answer();
        Call("MaintainHostReturnFreshSave", 2100L);
        Call("MaintainHostReturnFreshSave", 2900L);
        Assert.True(Field<bool>("_hostReturnSaveReady"));
    }

    [Theory]
    [InlineData("load")]
    [InlineData("unreadable")]
    [InlineData("combat")]
    [InlineData("session")]
    [InlineData("refused")]
    [InlineData("module")]
    public void AckAndExactSlotCannotCrossLifecycleOrIdlenessChange(string change)
    {
        Call("BeginHostReturnFreshSave", 1000L, "test");
        WriteSave(Dedicated);
        Answer(change == "refused" ? CheatStatus.EngineRefused : CheatStatus.Applied);
        Call("MaintainHostReturnFreshSave", 1100L);
        if (change == "load") _bridge.Serial++;
        if (change == "unreadable") _bridge.SerialReadable = false;
        if (change == "combat") _bridge.Flags = PlayerState.FlagInCombat;
        if (change == "module") _bridge.Module = "tar_m02aa";
        if (change == "session") _manager.Disconnect();
        Call("MaintainHostReturnFreshSave", 2000L);
        Assert.False(Field<bool>("_hostReturnSaveReady"));
    }

    [Fact]
    public void SoloReturnVerifiesExactCharacterEvenWhenPeriodicWatchAlreadyCapturedIt()
    {
        SessionSaveWatch watch = new(_game, () => DateTime.UtcNow.AddMinutes(-1));
        SetField("_sessionSaveWatch", watch);
        SetField("_soloObservedHostModule", _bridge.Module);
        SetField("_soloReturnState", SoloReturnRejoinState.Initial with { OriginModule = _bridge.Module, InSoloLeg = true });
        Call("BeginSoloReturnCapture", 1000L);
        WriteSave(Dedicated);
        Answer();
        watch.ObserveClient(123, Dedicated);
        Assert.True(watch.CaptureNowWritten(Dedicated, out _));
        Assert.False(watch.CaptureNowWritten(Dedicated, out _));
        _bridge.TryReadLocalState(out PlayerState local);
        Assert.False((bool)Call("MaintainSoloReturnCapture", local, 1100L)!);
        Assert.True((bool)Call("MaintainSoloReturnCapture", local, 2000L)!);
        Assert.True(ClientCharacterPersistence.TryReadSidecar(Path.Combine(_game, "saves"), out GffStruct actual, out _, out _));
        Assert.True(actual.TryGetShort("CurrentHitPoints", out short hp));
        Assert.Equal(12, hp);
    }

    [Theory]
    [InlineData("_ownLoadVerdictPending")]
    [InlineData("_ownLoadCaptureRequiresConfirmedSave")]
    [InlineData("_verifiedOwnLoadAwaitingRejoin")]
    [InlineData("_rejoinState")]
    public void SaveLoadCoordinationBlocksNewRequestAndInvalidatesAlreadyRequestedSave(string field)
    {
        object busy = field == "_rejoinState" ? new RejoinDecisionState(RejoinPhase.InFlight) : true;
        object idle = field == "_rejoinState" ? RejoinDecisionState.Idle : false;
        SetField(field, busy);
        Call("BeginHostReturnFreshSave", 1000L, "test");
        Assert.Empty(_bridge.Requests);
        SetField(field, idle);
        Call("BeginHostReturnFreshSave", 1100L, "test");
        WriteSave(Dedicated);
        Answer();
        Call("MaintainHostReturnFreshSave", 1200L);
        SetField(field, busy);
        Call("MaintainHostReturnFreshSave", 2000L);
        Assert.False(Field<bool>("_hostReturnSaveReady"));
    }

    [Fact]
    public void ExistingDedicatedArchiveCannotCountWithoutRewriteAndWrongModuleCannotCount()
    {
        WriteSave(Path.Combine(_game, "saves", "000002 - Co-op"));
        Call("BeginHostReturnFreshSave", 1000L, "test");
        Answer();
        Call("MaintainHostReturnFreshSave", 1100L);
        Call("MaintainHostReturnFreshSave", 2000L);
        Assert.False(Field<bool>("_hostReturnSaveReady"));
        WriteSave(Dedicated, "tar_m02aa");
        File.SetLastWriteTimeUtc(Path.Combine(Dedicated, "SAVEGAME.sav"), DateTime.UtcNow.AddSeconds(1));
        Call("MaintainHostReturnFreshSave", 2100L);
        Call("MaintainHostReturnFreshSave", 2900L);
        Assert.False(Field<bool>("_hostReturnSaveReady"));
    }

    private static void WriteSave(string directory, string module = "end_m01aa")
    {
        Directory.CreateDirectory(directory);
        GffWriter ifo = new("IFO ");
        ifo.Root.AddList("Mod_PlayerList").Add(48813).SetInt("IsPC", 1)
            .SetInt("Mod_IsPrimaryPlr", 1).SetShort("CurrentHitPoints", 12);
        File.WriteAllBytes(Path.Combine(directory, "SAVEGAME.sav"),
            Erf(module, 2057, Erf("Module", 2014, ifo.ToBytes())));
    }

    private static byte[] Erf(string name, int type, byte[] data)
    {
        byte[] result = new byte[192 + data.Length];
        Encoding.ASCII.GetBytes("SAV V1.0").CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x18), 160);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x1C), 184);
        Encoding.ASCII.GetBytes(name).CopyTo(result, 160);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(180), (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(184), 192);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(188), (uint)data.Length);
        data.CopyTo(result, 192);
        return result;
    }

    private sealed class SaveBridge : IGameBridge
    {
        public List<CheatRequest> Requests = [];
        public uint Serial = 17;
        public bool SerialReadable = true;
        public uint Answered;
        public int Status;
        public uint Flags;
        public string Module = "end_m01aa";
        public string Description => "return-save-test";
        public bool IsAttached => true;
        public bool CanWriteRemotes => true;
        public bool SupportsCheats => true;
        public bool TryReadLocalState(out PlayerState state)
        { state = new(Vector3f.Zero, 0, Module, 100, 0, Flags: Flags); return true; }
        public bool TryReadWorldLoadSerial(out uint serial) { serial = Serial; return SerialReadable; }
        public bool TryReadCurrentModule(out string module) { module = Module; return true; }
        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;
        public bool TryRequestCheat(CheatRequest request, out uint serial)
        { Requests.Add(request); serial = (uint)Requests.Count; return true; }
        public bool TryReadCheatAnswer(out uint applied, out int status, out string detail)
        { applied = Answered; status = Status; detail = "test"; return true; }
        public void Dispose() { }
    }
}
