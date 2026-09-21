using System.Buffers.Binary;
using System.Text;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure edge detector <see cref="SaveIdentityWatch"/> underlies BOTH roadmap item 14
/// phase 1 live triggers: a client's own save changing (capture to the "resume tomorrow"
/// sidecar) and a host's own save changing mid-session (the refusal <see cref="SessionSaveWatch"/>
/// decides). No filesystem, no peer comparison — each side only ever watches itself.
/// </summary>
public sealed class SaveIdentityWatchTests
{
    [Fact]
    public void AnUnresolvedDigestNeverEstablishesOrChanges()
    {
        SaveIdentityWatchState state = SaveIdentityWatchState.Initial;

        (state, bool changed) = SaveIdentityWatch.Observe(state, 0);

        Assert.False(state.Established);
        Assert.False(changed);
    }

    [Fact]
    public void TheFirstRealReadingBecomesTheBaselineNotAChange()
    {
        SaveIdentityWatchState state = SaveIdentityWatchState.Initial;

        (state, bool changed) = SaveIdentityWatch.Observe(state, 0xAAAA);

        Assert.True(state.Established);
        Assert.Equal(0xAAAAu, state.Baseline);
        Assert.False(changed); // nothing to change FROM yet
    }

    [Fact]
    public void TheSameDigestRepeatedIsNeverAChange()
    {
        SaveIdentityWatchState state = SaveIdentityWatchState.Initial;
        (state, _) = SaveIdentityWatch.Observe(state, 0xAAAA);

        for (int i = 0; i < 5; i++)
        {
            (state, bool changed) = SaveIdentityWatch.Observe(state, 0xAAAA);
            Assert.False(changed);
        }
    }

    [Fact]
    public void ADifferentDigestAfterEstablishingIsTheEdge()
    {
        SaveIdentityWatchState state = SaveIdentityWatchState.Initial;
        (state, _) = SaveIdentityWatch.Observe(state, 0xAAAA);

        (state, bool changed) = SaveIdentityWatch.Observe(state, 0xBBBB);

        Assert.True(changed);
        Assert.Equal(0xBBBBu, state.Baseline);
    }

    [Fact]
    public void AChangeReBaselinesSoItFiresOnlyOnce()
    {
        SaveIdentityWatchState state = SaveIdentityWatchState.Initial;
        (state, _) = SaveIdentityWatch.Observe(state, 0xAAAA);
        (state, bool firstChange) = SaveIdentityWatch.Observe(state, 0xBBBB);
        Assert.True(firstChange);

        (state, bool secondTick) = SaveIdentityWatch.Observe(state, 0xBBBB);
        Assert.False(secondTick);
    }

    [Fact]
    public void AnUnresolvedTickInTheMiddleDoesNotResetOrFalselyFireTheBaseline()
    {
        SaveIdentityWatchState state = SaveIdentityWatchState.Initial;
        (state, _) = SaveIdentityWatch.Observe(state, 0xAAAA);

        // A momentary "unknown" (SaveDigestCache mid-recompute) must not look like a change,
        // and must not erase the established baseline.
        (state, bool changedOnUnknown) = SaveIdentityWatch.Observe(state, 0);
        Assert.False(changedOnUnknown);
        Assert.Equal(0xAAAAu, state.Baseline);

        (state, bool changedAfter) = SaveIdentityWatch.Observe(state, 0xAAAA);
        Assert.False(changedAfter);
    }
}

/// <summary>
/// <see cref="SessionSaveWatch"/> wires <see cref="SaveIdentityWatch"/> to the two roadmap
/// item 14 phase 1 reactions. The HOST half is the "refusal decision as a pure rule" the
/// design calls for: <see cref="SessionSaveWatch.ObserveHost"/> takes nothing but a digest
/// value and returns nothing but a bool, so it is fully testable without a game, a session or
/// a network. The CLIENT half additionally needs a save directory to capture FROM, so those
/// cases build one with the same minimal helper <c>ClientCharacterPersistenceTests</c> uses.
/// </summary>
public sealed class SessionSaveWatchTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void HostEstablishingItsBaselineIsNeverARefusal()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());

        Assert.False(watch.ObserveHost(0)); // still unresolved
        Assert.False(watch.ObserveHost(0x1234)); // the first real reading: baseline, not a refusal
        Assert.False(watch.ObserveHost(0x1234)); // steady state
    }

    [Fact]
    public void HostsOwnSaveChangingMidSessionIsRefusedExactlyOnce()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());

        Assert.False(watch.ObserveHost(0x1234)); // baseline
        Assert.False(watch.ObserveHost(0x1234)); // still playing normally

        Assert.True(watch.ObserveHost(0x5678)); // the host loaded something else — THE edge

        // Never re-fires for the same new value, and a further genuine change fires again.
        Assert.False(watch.ObserveHost(0x5678));
        Assert.True(watch.ObserveHost(0x9ABC));
    }

    [Fact]
    public void ResetForgetsTheBaselineForAFreshConnection()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        Assert.False(watch.ObserveHost(0x1234));

        watch.Reset();

        // Without the reset this would be a "change" from the old 0x1234 baseline; with it,
        // it is correctly treated as a brand new session's first reading.
        Assert.False(watch.ObserveHost(0x9999));
    }

    [Fact]
    public void ClientCaptureFiresOnlyOnASaveIdentityChange()
    {
        string save = BuildSave("Trask Ulgo");
        string saves = TempDirectory();

        SessionSaveWatch watch = new(gameDirectory: saves);

        Assert.Null(watch.ObserveClient(0, save)); // unresolved: nothing happens
        Assert.Null(watch.ObserveClient(0x1111, save)); // baseline: no capture yet

        string? outcome = watch.ObserveClient(0x2222, save); // a save happened
        Assert.NotNull(outcome);
        Assert.Contains("session-saved character", outcome, StringComparison.OrdinalIgnoreCase);

        Assert.True(ClientCharacterPersistence.TryReadSidecar(
            Path.Combine(saves, "saves"), out GffStruct read, out _, out _));
        Assert.Equal("Trask Ulgo", read.GetLocString("FirstName")!.Text());

        // Steady state again: no repeat capture until the identity changes once more.
        Assert.Null(watch.ObserveClient(0x2222, save));
    }

    [Fact]
    public void CaptureOnDisconnectIsBestEffortAndNeedsNoPriorChange()
    {
        string save = BuildSave("Bastila Shan");
        string saves = TempDirectory();
        SessionSaveWatch watch = new(gameDirectory: saves);

        // No ObserveClient call ever happened — CaptureOnDisconnect must still work.
        string? outcome = watch.CaptureOnDisconnect(save);
        Assert.NotNull(outcome);

        Assert.True(ClientCharacterPersistence.TryReadSidecar(
            Path.Combine(saves, "saves"), out GffStruct read, out _, out _));
        Assert.Equal("Bastila Shan", read.GetLocString("FirstName")!.Text());
    }

    [Fact]
    public void CaptureOnDisconnectWithNoCurrentSaveIsANoOp()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        Assert.Null(watch.CaptureOnDisconnect(null));
    }

    // ---- roadmap item 14 phase 2: save-vs-load classification -----------------

    [Fact]
    public void ObserveOwnVerdictEstablishingBaselinesIsNeverAVerdict()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());

        // Establishing the digest baseline and the load-serial baseline together, on the
        // connection's first tick, must never read as either a save or a load — this is
        // "the load that IS the adoption" case ClientLoadRejoinRule's own doc comment
        // describes: nothing to see yet.
        Assert.Equal(SaveOrLoadVerdict.None, watch.ObserveOwnVerdict(0x1234, loadSerialNow: 7, nowMs: 0));
        Assert.Equal(SaveOrLoadVerdict.None, watch.ObserveOwnVerdict(0x1234, loadSerialNow: 7, nowMs: 100));
    }

    [Fact]
    public void ObserveOwnVerdictClassifiesADigestChangeWithNoLoadEdgeAsASave()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        watch.ObserveOwnVerdict(0x1234, loadSerialNow: 1, nowMs: 0); // baseline both

        // The digest changed (an ordinary autosave-through-a-transition, or a real save) but
        // the load-serial did not move — a save, not a load.
        SaveOrLoadVerdict verdict = watch.ObserveOwnVerdict(0x5678, loadSerialNow: 1, nowMs: 100);

        Assert.Equal(SaveOrLoadVerdict.Save, verdict);
    }

    [Fact]
    public void ObserveOwnVerdictClassifiesACoincidingLoadEdgeAsALoad()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        watch.ObserveOwnVerdict(0x1234, loadSerialNow: 1, nowMs: 0); // baseline both

        SaveOrLoadVerdict verdict = watch.ObserveOwnVerdict(0x5678, loadSerialNow: 2, nowMs: 100);

        Assert.Equal(SaveOrLoadVerdict.Load, verdict);
    }

    [Fact]
    public void ObserveOwnVerdictSkipsATickWithNoSerialReadingInsteadOfEdgingOnAZero()
    {
        // The live 2026-09-02 shape (wipe -> host reload -> the client's own rejoin load):
        // the bridge re-scans for the plugin block while the load lands, the serial is
        // unreadable for a few ticks, and the adopted world's digest changes meanwhile.
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        watch.ObserveOwnVerdict(0x1234, loadSerialNow: 3, nowMs: 0); // baseline both

        Assert.Equal(SaveOrLoadVerdict.None, watch.ObserveOwnVerdict(0x1234, loadSerialNow: null, nowMs: 100));
        Assert.Equal(SaveOrLoadVerdict.None, watch.ObserveOwnVerdict(0x5678, loadSerialNow: null, nowMs: 200));

        // The block is back, the serial never actually moved: a save-shaped digest change,
        // NOT the Load verdict that started the second rejoin live.
        Assert.NotEqual(SaveOrLoadVerdict.Load, watch.ObserveOwnVerdict(0x5678, loadSerialNow: 3, nowMs: 300));

        // A genuine load edge after the gap still classifies.
        Assert.Equal(SaveOrLoadVerdict.Load, watch.ObserveOwnVerdict(0x9ABC, loadSerialNow: 4, nowMs: 400));
    }

    [Fact]
    public void ObserveOwnVerdictFedAZeroForAMissingBlockWouldHaveReadAsALoad()
    {
        // Documents WHY the nullable form exists: the old caller read WorldLoadSerial, which
        // is 0 with no block, and this is what the watch makes of that.
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        watch.ObserveOwnVerdict(0x1234, loadSerialNow: 3, nowMs: 0);
        watch.ObserveOwnVerdict(0x1234, loadSerialNow: 0, nowMs: 100);

        Assert.Equal(SaveOrLoadVerdict.Load, watch.ObserveOwnVerdict(0x5678, loadSerialNow: 3, nowMs: 200));
    }

    [Fact]
    public void ObserveRemoteVerdictWatchesAnIndependentPeerStream()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());

        // The local (own) watch and the remote (host) watch are independent: establishing
        // one's baseline must not affect the other, and each classifies its own edges.
        watch.ObserveOwnVerdict(0xAAAA, loadSerialNow: 1, nowMs: 0);
        watch.ObserveRemoteVerdict(0xBBBB, remoteLoadSerialNow: 1, nowMs: 0, out _);

        Assert.Equal(SaveOrLoadVerdict.Save, watch.ObserveOwnVerdict(0xAAAB, loadSerialNow: 1, nowMs: 100));
        Assert.Equal(
            SaveOrLoadVerdict.Load, watch.ObserveRemoteVerdict(0xBBBC, remoteLoadSerialNow: 2, nowMs: 100, out _));
    }

    [Fact]
    public void ObserveRemoteVerdictExposesTheRawSerialEdgeEvenWithoutAVerdict()
    {
        // The live 2026-09-02 shape: the host's serial ticks, its digest never changes. The
        // classifier says None (nothing to correlate) but HostWorldReplacedRule needs the edge.
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        watch.ObserveRemoteVerdict(0xBBBB, remoteLoadSerialNow: 1, nowMs: 0, out bool edge);
        Assert.False(edge); // the baseline is never an edge

        Assert.Equal(
            SaveOrLoadVerdict.None, watch.ObserveRemoteVerdict(0xBBBB, remoteLoadSerialNow: 2, nowMs: 100, out edge));
        Assert.True(edge);

        Assert.Equal(
            SaveOrLoadVerdict.None, watch.ObserveRemoteVerdict(0xBBBB, remoteLoadSerialNow: 2, nowMs: 200, out edge));
        Assert.False(edge);
    }

    [Fact]
    public void AnUnresolvedRemoteDigestIsNotAReadingForTheSerialEither()
    {
        // A fresh CoopClient reports WorldDigest.Empty until the host's first packet lands.
        // Baselining the serial on that 0 made the host's real serial (here 7) an edge one
        // tick later — live 2026-09-02, a just-rejoined client rejoined a second time.
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        watch.ObserveRemoteVerdict(0, remoteLoadSerialNow: 0, nowMs: 0, out bool edge);
        Assert.False(edge);

        watch.ObserveRemoteVerdict(0xBBBB, remoteLoadSerialNow: 7, nowMs: 100, out edge);
        Assert.False(edge);

        watch.ObserveRemoteVerdict(0xBBBB, remoteLoadSerialNow: 8, nowMs: 200, out edge);
        Assert.True(edge);
    }

    [Fact]
    public void ResetForgetsThePhase2WatchesToo()
    {
        SessionSaveWatch watch = new(gameDirectory: Path.GetTempPath());
        watch.ObserveOwnVerdict(0x1234, loadSerialNow: 1, nowMs: 0);
        watch.ObserveRemoteVerdict(0x5678, remoteLoadSerialNow: 1, nowMs: 0, out _);

        watch.Reset();

        // Without the reset these would read as changes from the old baselines; with it, the
        // next readings are a fresh connection's first — never a verdict.
        Assert.Equal(SaveOrLoadVerdict.None, watch.ObserveOwnVerdict(0x9999, loadSerialNow: 9, nowMs: 200));
        Assert.Equal(
            SaveOrLoadVerdict.None, watch.ObserveRemoteVerdict(0x8888, remoteLoadSerialNow: 8, nowMs: 200, out _));
    }

    // ---- helpers --------------------------------------------------------------

    private string BuildSave(string name)
    {
        string directory = Path.Combine(Path.GetTempPath(), "kotorcoop-savewatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);

        GffWriter ifo = new("IFO ");
        ifo.Root.SetDword("Mod_NextObjId0", 1).SetDword("Mod_NextObjId1", 0);

        GffWriterList players = ifo.Root.AddList("Mod_PlayerList");
        players.Add(48813)
            .SetDword("ObjectId", 0x7FFFFFFF)
            .SetInt("Mod_IsPrimaryPlr", 1)
            .SetInt("IsPC", 1)
            .SetLocString("FirstName", -1, (0, name))
            .SetShort("CurrentHitPoints", 12)
            .SetFloat("XPosition", 21.0f)
            .AddStruct("FollowInfo", 3)
            .SetVector("LastLeaderPos", 1f, 2f, 3f);

        byte[] inner = BuildErf("SAV ", ("Module", 2014, ifo.ToBytes()));

        File.WriteAllBytes(
            Path.Combine(directory, "SAVEGAME.sav"),
            BuildErf("SAV ", ("end_m01aa", 2057, inner)));

        File.WriteAllBytes(Path.Combine(directory, "GLOBALVARS.res"), [1, 2, 3, 4]);
        GffWriter party = new("PT");
        party.Root.AddList("PT_PAZAAKCARDS").Add(0).SetInt("PT_PAZAAKCOUNT", 2);
        party.Root.AddList("PT_PAZSIDELIST").Add(0).SetInt("PT_PAZSIDECARD", -1);
        File.WriteAllBytes(Path.Combine(directory, "PARTYTABLE.res"), party.ToBytes());
        File.WriteAllBytes(Path.Combine(directory, "savenfo.res"), [9]);

        return directory;
    }

    private string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kotorcoop-savewatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    private static byte[] BuildErf(string fileType, params (string Resref, int Type, byte[] Data)[] members)
    {
        int count = members.Length;
        int keysOffset = 160;
        int valuesOffset = keysOffset + (24 * count);
        int dataOffset = valuesOffset + (8 * count);

        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes(fileType.PadRight(4)).CopyTo(file, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(file, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x10, 4), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x18, 4), (uint)keysOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x1C, 4), (uint)valuesOffset);

        int offset = dataOffset;
        for (int i = 0; i < count; i++)
        {
            (string resref, int type, byte[] data) = members[i];
            Encoding.ASCII.GetBytes(resref).CopyTo(file, keysOffset + (24 * i));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(keysOffset + (24 * i) + 16, 4), (uint)i);
            file[keysOffset + (24 * i) + 20] = (byte)type;
            file[keysOffset + (24 * i) + 21] = (byte)(type >> 8);

            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(valuesOffset + (8 * i), 4), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(valuesOffset + (8 * i) + 4, 4), (uint)data.Length);
            data.CopyTo(file, offset);
            offset += data.Length;
        }

        return file;
    }
}
