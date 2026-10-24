using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Shipping the host's world to a joiner, over real loopback sockets — the same
/// configuration the mod runs in, not a mocked stream.
///
/// What matters here is less the happy path than the refusals: the receiver is being handed
/// bytes by a remote machine, and the two fields that could hurt it are the file NAME (which
/// becomes a path) and the declared SIZE (which becomes an allocation).
/// </summary>
public sealed class WorldTransferTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();

        foreach (string directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leaked temp directory must not fail the suite.
            }
        }
    }

    // ---- the round trip --------------------------------------------------------

    [Fact]
    public async Task LegitimateModArchiveAboveOldSixteenMiBLimitTransfersOverTcp()
    {
        byte[] archive = Bytes(17 * 1024 * 1024, seed: 17);
        int port = await ServeAsync([new("SAVEGAME.sav", archive)], SessionPassword.None);
        var received = await WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token);
        Assert.Equal(archive, Assert.Single(received.Files).Data);
    }

    [Fact]
    public async Task AboveSixtyFourMiBFileRefusesBeforePayloadAllocation()
    {
        int port = await ServeHostileAsync("SAVEGAME.sav", 64u * 1024 * 1024 + 1, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token));
    }

    [Fact]
    public async Task AReachabilityProbeIsNotLoggedAsAFailedTransfer()
    {
        // 2026-10-24 host bundle: every join logged "world transfer failed: ... closed after 0
        // of 8 bytes" for the joiner's bare-connect port probe, then sent the world normally.
        List<string> log = [];
        int port = FreePort();
        _ = WorldTransfer.ServeAsync(
            [new("SAVEGAME.sav", Bytes(64, seed: 3))], port, SessionPassword.None, 0,
            line => { lock (log) { log.Add(line); } }, _cts.Token);
        await WaitUntilListeningAsync(port);

        using (TcpClient probe = new())
        {
            await probe.ConnectAsync(Local(port), _cts.Token);
        }

        await WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token);

        // The server logs "world sent" after its flush, which can land after the fetch returns.
        for (int i = 0; i < 100; i++)
        {
            lock (log)
            {
                if (log.Exists(line => line.StartsWith("world sent", StringComparison.Ordinal)))
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        lock (log)
        {
            Assert.DoesNotContain(log, line => line.Contains("failed", StringComparison.Ordinal));
            Assert.Contains(log, line => line.StartsWith("world sent", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AJoinerFetchesTheHostsWorld()
    {
        IReadOnlyList<WorldFile> world =
        [
            new("SAVEGAME.sav", Bytes(1024, seed: 7)),
            new("GLOBALVARS.res", Bytes(64, seed: 9)),
            new("PARTYTABLE.res", Bytes(32, seed: 11)),
            new("savenfo.res", Bytes(16, seed: 13)),
        ];

        int port = await ServeAsync(world, SessionPassword.None);

        IReadOnlyList<WorldFile> received = (await WorldTransfer.FetchAsync(
            Local(port), SessionPassword.None, _cts.Token)).Files;

        Assert.Equal(world.Count, received.Count);
        for (int i = 0; i < world.Count; i++)
        {
            Assert.Equal(world[i].Name, received[i].Name);
            Assert.Equal(world[i].Data, received[i].Data);
        }
    }

    [Fact]
    public async Task ASaveSizedWorldSurvivesTheTransfer()
    {
        // A real save is ~654 KB without the screenshot, which is well past anything that
        // fits in one socket read — the framing has to reassemble.
        IReadOnlyList<WorldFile> world = [new("SAVEGAME.sav", Bytes(584_228, seed: 3))];

        int port = await ServeAsync(world, SessionPassword.None);
        IReadOnlyList<WorldFile> received = (await WorldTransfer.FetchAsync(
            Local(port), SessionPassword.None, _cts.Token)).Files;

        Assert.Single(received);
        Assert.Equal(world[0].Data, received[0].Data);
    }

    [Fact]
    public async Task ServingSurvivesAJoinerThatDisconnectsMidTransfer()
    {
        // The listener runs for the life of the session; one abandoned transfer must not
        // end it, because the joiner's own retry is the recovery.
        int port = await ServeAsync([new("SAVEGAME.sav", Bytes(256, seed: 5))], SessionPassword.None);

        using (TcpClient rude = new())
        {
            await rude.ConnectAsync(Local(port), _cts.Token);
            await rude.GetStream().WriteAsync(new byte[] { 0x4B }, _cts.Token);
        }

        IReadOnlyList<WorldFile> received = (await WorldTransfer.FetchAsync(
            Local(port), SessionPassword.None, _cts.Token)).Files;

        Assert.Single(received);
    }

    [Fact]
    public async Task TheHostStatesTheIdentityOfTheWorldItServes()
    {
        // Transfer v2. Without this the joiner's self-check ("is what I staged what I was
        // sent?") cannot run at all, because nothing ever told it what to expect.
        int port = await ServeAsync(
            [new("SAVEGAME.sav", Bytes(64, seed: 31))], SessionPassword.None, saveIdentity: 0x155107BD);

        FetchedWorld fetched = await WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token);

        Assert.Equal(0x155107BDu, fetched.SaveIdentity);
        Assert.Single(fetched.Files);
    }

    [Fact]
    public async Task AHostThatCouldNotFingerprintItsWorldSaysSoWithZero()
    {
        // The house rule on the transfer too: unknown is a legal answer that skips the
        // check, never one that blocks the transfer.
        int port = await ServeAsync([new("SAVEGAME.sav", Bytes(64, seed: 32))], SessionPassword.None);

        FetchedWorld fetched = await WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token);

        Assert.Equal(0u, fetched.SaveIdentity);
        Assert.Single(fetched.Files);
    }

    // ---- refusals --------------------------------------------------------------

    [Fact]
    public async Task TheWrongPasswordIsRefused()
    {
        int port = await ServeAsync([new("SAVEGAME.sav", Bytes(16, seed: 1))], SessionPassword.Hash("secret"));

        WorldTransferRefusedException error = await Assert.ThrowsAsync<WorldTransferRefusedException>(
            () => WorldTransfer.FetchAsync(Local(port), SessionPassword.Hash("guess"), _cts.Token));

        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostWithNoWorldRefusesRatherThanSendingNothing()
    {
        int port = await ServeAsync([], SessionPassword.None);

        WorldTransferRefusedException error = await Assert.ThrowsAsync<WorldTransferRefusedException>(
            () => WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token));

        Assert.Contains("no world", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHostWithNoWorldIsDistinguishableFromEveryOtherRefusal()
    {
        // WorldAdoption needs to react differently to "the host has nothing to serve yet"
        // (the first-time-host UX gap) than to a version/password mismatch or a bug — this is
        // what makes that distinction possible without parsing the message text.
        int port = await ServeAsync([], SessionPassword.None);

        WorldTransferRefusedException error = await Assert.ThrowsAsync<WorldTransferRefusedException>(
            () => WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token));

        Assert.True(error.NoWorldAvailable);
    }

    [Fact]
    public async Task TheWrongPasswordIsNotFlaggedAsNoWorldAvailable()
    {
        int port = await ServeAsync([new("SAVEGAME.sav", Bytes(16, seed: 1))], SessionPassword.Hash("secret"));

        WorldTransferRefusedException error = await Assert.ThrowsAsync<WorldTransferRefusedException>(
            () => WorldTransfer.FetchAsync(Local(port), SessionPassword.Hash("guess"), _cts.Token));

        Assert.False(error.NoWorldAvailable);
    }

    [Fact]
    public async Task AForeignServerOnThePortIsRejected()
    {
        // Something else listening on the port must cost a clear error, not a hang or a
        // pile of garbage written into the saves directory.
        using TcpListener foreign = new(IPAddress.Loopback, 0);
        foreign.Start();
        int port = ((IPEndPoint)foreign.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using TcpClient client = await foreign.AcceptTcpClientAsync(_cts.Token);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404\r\n\r\n"), _cts.Token);
        });

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token));

        Assert.Contains("co-op host", error.Message);
        foreign.Stop();
    }

    [Fact]
    public async Task ATraversingFileNameIsRefused()
    {
        // The name is the one field of this protocol that becomes a path. A malicious host
        // offering "..\..\swkotor.exe" must not be able to write outside the save slot.
        int port = await ServeHostileAsync(name: @"..\..\swkotor.exe", declaredSize: 4, payload: [1, 2, 3, 4]);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token));

        Assert.Contains("file name", error.Message);
    }

    [Fact]
    public async Task AnAbsurdDeclaredSizeIsRefusedBeforeAllocating()
    {
        int port = await ServeHostileAsync(name: "SAVEGAME.sav", declaredSize: 0xFFFF_FFF0, payload: []);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token));

        Assert.Contains("bytes", error.Message);
    }

    [Fact]
    public async Task ATruncatedTransferIsAnErrorNotAPartialWorld()
    {
        // Half a save written into the slot would be worse than none: it would load, or
        // half-load, as a world neither player has.
        int port = await ServeHostileAsync(name: "SAVEGAME.sav", declaredSize: 4096, payload: [1, 2, 3]);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => WorldTransfer.FetchAsync(Local(port), SessionPassword.None, _cts.Token));

        Assert.Contains("closed", error.Message);
    }

    // ---- staging what arrived --------------------------------------------------

    [Fact]
    public void WritingAWorldReplacesTheSlotEntirely()
    {
        // The joiner's previous adopted world must not survive underneath the new one: a
        // leftover GLOBALVARS.res from a different host is exactly the kind of half-state
        // the id-keyed channels cannot see is wrong.
        string slot = TempDirectory();
        File.WriteAllText(Path.Combine(slot, "leftover.res"), "stale");

        WorldTransfer.WriteSaveDirectory(slot, [new("SAVEGAME.sav", Bytes(32, seed: 2))]);

        Assert.False(File.Exists(Path.Combine(slot, "leftover.res")));
        Assert.True(File.Exists(Path.Combine(slot, "SAVEGAME.sav")));
    }

    [Fact]
    public void StagingNeutralisesAHostAutosavesPifo()
    {
        // The trap: an adopted AUTOSAVE carries the host's character as a pifo, which
        // LoadPCAutoSave would copy over the joiner's own. Staging has to defuse it.
        byte[] savegame = BuildErf("SAV ",
            ("Module", 2014, new GffWriter("IFO ").ToBytes()),
            ("pifo", 2014, new GffWriter("IFO ").ToBytes()));

        string slot = TempDirectory();
        WorldTransfer.WriteSaveDirectory(slot, [new("SAVEGAME.sav", savegame)]);

        byte[] staged = File.ReadAllBytes(Path.Combine(slot, "SAVEGAME.sav"));
        Assert.False(ErfArchive.TryFindMember(staged, 2014, "pifo", out _));
        Assert.True(ErfArchive.TryFindMember(staged, 2014, "Module", out _));

        // ...and the caller's own buffer is untouched, since on a host that list is live.
        Assert.True(ErfArchive.TryFindMember(savegame, 2014, "pifo", out _));
    }

    [Fact]
    public void TheScreenshotDoesNotTravel()
    {
        // A third of a save's bytes, and pure GUI decoration for a slot the mod loads
        // programmatically.
        string save = TempDirectory();
        File.WriteAllBytes(Path.Combine(save, "SAVEGAME.sav"), Bytes(16, seed: 1));
        File.WriteAllBytes(Path.Combine(save, "Screen.tga"), Bytes(196_626, seed: 4));

        IReadOnlyList<WorldFile> world = WorldTransfer.ReadSaveDirectory(save);

        Assert.Single(world);
        Assert.Equal("SAVEGAME.sav", world[0].Name);
    }

    [Fact]
    public async Task AWorldReadFromDiskArrivesIdenticalOnTheOtherSide()
    {
        // End to end: a save directory on one machine becomes the same save directory on
        // the other, which is the whole claim of world adoption's transport half.
        string source = TempDirectory();
        File.WriteAllBytes(Path.Combine(source, "SAVEGAME.sav"), Bytes(4096, seed: 21));
        File.WriteAllBytes(Path.Combine(source, "GLOBALVARS.res"), Bytes(57_656, seed: 22));
        File.WriteAllBytes(Path.Combine(source, "PARTYTABLE.res"), Bytes(11_449, seed: 23));
        File.WriteAllBytes(Path.Combine(source, "savenfo.res"), Bytes(673, seed: 24));

        int port = await ServeAsync(WorldTransfer.ReadSaveDirectory(source), SessionPassword.None);
        IReadOnlyList<WorldFile> received = (await WorldTransfer.FetchAsync(
            Local(port), SessionPassword.None, _cts.Token)).Files;

        string destination = TempDirectory();
        WorldTransfer.WriteSaveDirectory(destination, received);

        foreach (string path in Directory.GetFiles(source))
        {
            string mirrored = Path.Combine(destination, Path.GetFileName(path));
            Assert.True(File.Exists(mirrored), $"{Path.GetFileName(path)} did not arrive");
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(mirrored));
        }
    }

    // ---- helpers ---------------------------------------------------------------

    private static IPEndPoint Local(int port) => new(IPAddress.Loopback, port);

    /// <summary>Starts a server on a free port and returns it, once it is accepting.</summary>
    private async Task<int> ServeAsync(
        IReadOnlyList<WorldFile> world, uint passwordHash, uint saveIdentity = 0)
    {
        int port = FreePort();
        _ = WorldTransfer.ServeAsync(world, port, passwordHash, saveIdentity, log: null, _cts.Token);
        await WaitUntilListeningAsync(port);
        return port;
    }

    /// <summary>
    /// A server that speaks the framing correctly up to one deliberately malformed file
    /// header, so the receiver's bounds and name checks can be reached at all.
    /// </summary>
    private async Task<int> ServeHostileAsync(string name, uint declaredSize, byte[] payload)
    {
        int port = FreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(_cts.Token);
                await using NetworkStream stream = client.GetStream();

                byte[] request = new byte[8];
                int read = 0;
                while (read < request.Length)
                {
                    int n = await stream.ReadAsync(request.AsMemory(read), _cts.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    read += n;
                }

                // Transfer v2 framing: an 8-byte reply header (magic, version, status, then
                // the host's save identity), a 2-byte file count, then one 6-byte file prefix.
                byte[] nameBytes = Encoding.Latin1.GetBytes(name);
                byte[] reply = new byte[8 + 2 + 6 + nameBytes.Length];
                reply[0] = 0x4B;                                    // magic low  ('K')
                reply[1] = 0x57;                                    // magic high ('W')
                reply[2] = 2;                                       // version
                reply[3] = 0;                                       // status ok
                BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4, 4), 0u); // identity: unknown
                reply[8] = 1;                                       // file count = 1
                reply[10] = (byte)nameBytes.Length;
                reply[11] = (byte)(nameBytes.Length >> 8);
                BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12, 4), declaredSize);
                nameBytes.CopyTo(reply, 16);

                await stream.WriteAsync(reply, _cts.Token);
                await stream.WriteAsync(payload, _cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
                // The receiver hanging up on a refusal is the expected end of this server.
            }
            finally
            {
                listener.Stop();
            }
        });

        return port;
    }

    private static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task WaitUntilListeningAsync(int port)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                using TcpClient probe = new();
                await probe.ConnectAsync(Local(port), _cts.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(10, _cts.Token);
            }
        }

        throw new TimeoutException($"nothing came up on :{port}");
    }

    /// <summary>Deterministic pseudo-random bytes, so a mismatch names the byte rather than
    /// being invisible in a block of zeroes.</summary>
    private static byte[] Bytes(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private string TempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "kotorcoop-world-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }

    /// <summary>Lays out an ERF exactly as <see cref="ErfArchive"/> decodes it.</summary>
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
            BinaryPrimitives.WriteUInt32LittleEndian(
                file.AsSpan(keysOffset + (24 * i) + 16, 4), (uint)i);
            file[keysOffset + (24 * i) + 20] = (byte)type;
            file[keysOffset + (24 * i) + 21] = (byte)(type >> 8);

            BinaryPrimitives.WriteUInt32LittleEndian(
                file.AsSpan(valuesOffset + (8 * i), 4), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(
                file.AsSpan(valuesOffset + (8 * i) + 4, 4), (uint)data.Length);
            data.CopyTo(file, offset);
            offset += data.Length;
        }

        return file;
    }
}
