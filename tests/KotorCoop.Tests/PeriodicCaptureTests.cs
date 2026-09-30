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

public sealed class PeriodicCaptureTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "kcoop-periodic-" + Guid.NewGuid().ToString("N"));
    private readonly SaveBridge _bridge = new();
    private readonly List<string> _log = [];
    private readonly SessionManager _manager;
    private readonly CoopCommandDispatcher _dispatcher;
    private string Saves => Path.Combine(_game, "saves");
    private string Dedicated => Path.Combine(Saves, "000002 - Co-op");
    private string Quick => Path.Combine(Saves, "000000 - QUICKSAVE", "SAVEGAME.sav");

    public PeriodicCaptureTests()
    {
        WriteSave(Path.Combine(Saves, "000000 - QUICKSAVE"), "Original quicksave");
        Directory.CreateDirectory(Path.Combine(Saves, "000001 - AUTOSAVE"));
        _manager = new(_bridge, new AgentSettings());
        _manager.Connect(true, null, new SessionSettings { PlayerName = "Tester", Port = 0 });
        _dispatcher = new(_manager, _bridge, 27140, _ => new SessionSettings(), gameDirectory: _game);
        _dispatcher.Log = _log.Add;
        Set("_characterCaptureIntervalMin", 1);
        Set("_captureLastMs", 1L);
    }

    public void Dispose() { _manager.Dispose(); Directory.Delete(_game, true); }
    private object? Call(string method, params object[] args) => typeof(CoopCommandDispatcher)
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_dispatcher, args);
    private void Set(string field, object value) => typeof(CoopCommandDispatcher)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_dispatcher, value);
    private T Field<T>(string field) => (T)typeof(CoopCommandDispatcher)
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_dispatcher)!;
    private void Tick(long now) { _bridge.TryReadLocalState(out PlayerState state); Call("MaintainPeriodicCapture", state, now, Saves); }
    private void Answer(int status = 1) { _bridge.Answered = (uint)_bridge.Requests.Count; _bridge.Status = status; Call("ReflectCheatAnswer"); }
    private void Land() { WriteSave(Dedicated, "Current own character"); Assert.True(NativeSaveArtifactRule.Complete(Dedicated)); _bridge.Completion = 4; }

    [Fact]
    public void RealDispatcherRequiresAckNativeCompletionAndStableFourFiles()
    {
        byte[] quickHash = SHA256.HashData(File.ReadAllBytes(Quick)); DateTime quickStamp = File.GetLastWriteTimeUtc(Quick);
        Tick(60001);
        Assert.Equal(CheatKinds.CoopSave, Assert.Single(_bridge.Requests).Kind);
        Assert.Null(Call("BeginReturnSave")); // A waiting capture cannot overwrite this request.
        Land(); Tick(60100); Tick(62000);
        Assert.False(File.Exists(Path.Combine(Saves, "kotorcoop-character.pifo")));
        Answer(); Tick(62100); Tick(62849);
        Assert.False(File.Exists(Path.Combine(Saves, "kotorcoop-character.pifo")));
        Tick(62850);
        Assert.True(ClientCharacterPersistence.TryReadSidecar(Saves, out GffStruct pc, out _, out string diagnostic), diagnostic + " / " + string.Join(" | ", _log));
        Assert.Equal("Current own character", pc.GetLocString("FirstName")!.Text());
        Assert.Equal(0L, Field<long>("_captureRequestedMs"));
        Assert.Equal(quickHash, SHA256.HashData(File.ReadAllBytes(Quick)));
        Assert.Equal(quickStamp, File.GetLastWriteTimeUtc(Quick));
        Assert.False(Directory.Exists(Path.Combine(Saves, ".kcoop-quicksave-holdback")));
    }

    [Fact]
    public void ReadableFreshSaveWithOnlyAckNeverCapturesOrFallsBack()
    {
        Tick(60001); WriteSave(Dedicated, "Uncompleted own character"); Answer();
        Tick(60200); Tick(61200);
        Assert.False(File.Exists(Path.Combine(Saves, "kotorcoop-character.pifo")));
        Tick(70001);
        Assert.Equal(0L, Field<long>("_captureRequestedMs"));
        Assert.False(File.Exists(Path.Combine(Saves, "kotorcoop-character.pifo")));
        Assert.True(File.Exists(Quick));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("serial")]
    [InlineData("module")]
    [InlineData("completion_serial")]
    [InlineData("completion_epoch")]
    [InlineData("completion_directory")]
    public void ChangedWorldOrWrongNativeCompletionRefusesCapture(string mismatch)
    {
        Tick(60001); Land(); Answer();
        switch (mismatch)
        {
            case "epoch": _bridge.Epoch++; break;
            case "serial": _bridge.Serial++; break;
            case "module": _bridge.Module = "tar_m02aa"; break;
            case "completion_serial": _bridge.CompletionSerial++; break;
            case "completion_epoch": _bridge.CompletionEpoch++; break;
            case "completion_directory": _bridge.CompletionDirectory = "000099 - Other"; break;
        }
        Tick(60200); Tick(61200); Tick(70001);
        Assert.False(File.Exists(Path.Combine(Saves, "kotorcoop-character.pifo")));
        Assert.True(File.Exists(Quick));
    }

    [Fact]
    public void FourthOutputChangingRestartsWindowInActualDispatcher()
    {
        Tick(60001); Land(); Answer(); Tick(60200);
        var globals = new GffWriter("GVT "); globals.Root.SetInt("changed", 1);
        File.WriteAllBytes(Path.Combine(Dedicated, "GLOBALVARS.res"), globals.ToBytes());
        Tick(60900); Tick(61649);
        Assert.False(File.Exists(Path.Combine(Saves, "kotorcoop-character.pifo")));
        Tick(61650);
        Assert.True(File.Exists(Path.Combine(Saves, "kotorcoop-character.pifo")));
    }

    [Fact]
    public void NativeRefusalKeepsExistingCheckpointAndQuickSave()
    {
        Assert.True(ClientCharacterPersistence.TryWriteSidecar(Saves, Gff.Parse(new GffWriter("IFO ").ToBytes()).Root, 1, DateTime.UtcNow, out _));
        string sidecar = Path.Combine(Saves, "kotorcoop-character.pifo"); byte[] before = File.ReadAllBytes(sidecar);
        Tick(60001); Answer(5); Tick(60200);
        Assert.Equal(before, File.ReadAllBytes(sidecar));
        Assert.Equal(0L, Field<long>("_captureRequestedMs"));
        Assert.True(File.Exists(Quick));
    }

    [Theory]
    [InlineData("_soloCapturePending")]
    [InlineData("_soloCaptureWaiting")]
    [InlineData("_ownLoadVerdictPending")]
    [InlineData("_verifiedOwnLoadAwaitingRejoin")]
    public void OtherCaptureOrLoadOwnershipDefersPeriodicStart(string field)
    {
        Set(field, true); Tick(60001);
        Assert.Empty(_bridge.Requests);
        Assert.Equal(0L, Field<long>("_captureRequestedMs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettledOwnLoadGuardRequiresCompletedCaptureOfTheSameOwnLoadEpoch(bool epochChanges)
    {
        Set("_ownLoadCaptureRequiresConfirmedSave", true);
        Set("_ownLoadCaptureEpoch", 5u);
        Tick(60001); Assert.Equal(CheatKinds.CoopSave, Assert.Single(_bridge.Requests).Kind);
        Land(); Answer(); Tick(60200);
        if (epochChanges) Set("_ownLoadCaptureEpoch", 6u);
        Tick(60950);
        Assert.Equal(epochChanges, Field<bool>("_ownLoadCaptureRequiresConfirmedSave"));
        Assert.Equal(!epochChanges, File.Exists(Path.Combine(Saves, "kotorcoop-character.pifo")));
    }
    private static void WriteSave(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        GffWriter ifo = new("IFO "); ifo.Root.SetDword("Mod_NextObjId0", 1).SetDword("Mod_NextObjId1", 0);
        ifo.Root.AddList("Mod_PlayerList").Add(48813).SetDword("ObjectId", 0x7FFFFFFF).SetInt("IsPC", 1).SetInt("Mod_IsPrimaryPlr", 1)
            .SetLocString("FirstName", -1, (0, name)).SetShort("CurrentHitPoints", 12);
        byte[] module = Erf(("Module", 2014, ifo.ToBytes()), ("Module", 2023, new GffWriter("GIT ").ToBytes()));
        File.WriteAllBytes(Path.Combine(directory, "SAVEGAME.sav"), Erf(("end_m01aa", 2057, module)));
        GffWriter info = new("NFO "); info.Root.SetString("LASTMODULE", "end_m01aa");
        File.WriteAllBytes(Path.Combine(directory, "savenfo.res"), info.ToBytes());
        GffWriter party = new("PT  "); party.Root.AddList("PT_PAZAAKCARDS"); party.Root.AddList("PT_PAZSIDELIST");
        File.WriteAllBytes(Path.Combine(directory, "PARTYTABLE.res"), party.ToBytes());
        File.WriteAllBytes(Path.Combine(directory, "GLOBALVARS.res"), new GffWriter("GVT ").ToBytes());
    }

    private static byte[] Erf(params (string Name, int Type, byte[] Data)[] entries)
    {
        int count = entries.Length, keys = 160, values = keys + 24 * count, offset = values + 8 * count;
        byte[] result = new byte[offset + entries.Sum(e => e.Data.Length)]; Encoding.ASCII.GetBytes("SAV V1.0").CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x18), (uint)keys);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x1C), (uint)values);
        for (int i = 0; i < count; i++)
        {
            var entry = entries[i]; Encoding.ASCII.GetBytes(entry.Name).CopyTo(result, keys + 24 * i);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(keys + 24 * i + 16), (uint)i);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(keys + 24 * i + 20), (ushort)entry.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(values + 8 * i), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(values + 8 * i + 4), (uint)entry.Data.Length);
            entry.Data.CopyTo(result, offset); offset += entry.Data.Length;
        }
        return result;
    }

    private sealed class SaveBridge : IGameBridge
    {
        public List<CheatRequest> Requests = [];
        public uint Serial = 17, Epoch = 9, Completion = 2, CompletionSerial = 17, CompletionEpoch = 9, Answered;
        public int Status; public string Module = "end_m01aa", CompletionDirectory = "000002 - Co-op";
        public string Description => "periodic-save-test";
        public bool IsAttached => true;
        public bool CanWriteRemotes => true;
        public bool SupportsCheats => true;
        public bool TryReadLocalState(out PlayerState state) { state = new(Vector3f.Zero, 0, Module, 100, 0); return true; }
        public bool TryReadWorldLoadSerial(out uint serial) { serial = Serial; return true; }
        public bool TryReadLivenessEpoch(out uint epoch) { epoch = Epoch; return true; }
        public bool TryReadCurrentModule(out string module) { module = Module; return true; }
        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;
        public bool TryRequestCheat(CheatRequest request, out uint serial) { Requests.Add(request); serial = (uint)Requests.Count; return true; }
        public bool TryReadCheatAnswer(out uint applied, out int status, out string detail) { applied = Answered; status = Status; detail = "test"; return true; }
        public bool TryReadNativeSaveCompletion(out uint sequence, out string directory, out PartyRoster party)
        { sequence = Completion; directory = CompletionDirectory; party = new(0, 0, HostLoadSerial: CompletionSerial, HostNativeEpoch: CompletionEpoch); return true; }
        public void Dispose() { }
    }
}
