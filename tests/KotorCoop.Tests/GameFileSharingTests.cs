using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;
using GameFilesReader = KotorCoop.Memory.GameFiles.GameFiles;

namespace KotorCoop.Tests;

/// <summary>
/// Regression coverage for a real live-only bug: <c>GameFileIo.ReadSlice</c> (used for
/// module RIM/ERF/BIF reads) has always opened with <c>FileShare.ReadWrite</c>, with a
/// comment explaining why — "the running game keeps these files open; a reader demanding
/// exclusivity would fail the moment swkotor.exe is up". <c>KeyIndex.Load</c> (chitin.key),
/// the loose-override branch of <c>GameFiles.TryReadResource</c>, <c>InstallDigest</c>'s
/// Override\*.2da hashing, and <c>Tlk.Open</c> did not follow that rule: they used the
/// BCL's <c>File.ReadAllBytes</c>, whose default is <c>FileShare.Read</c> — a sharing
/// violation against a handle swkotor.exe holds with write access it never actually uses.
///
/// No existing unit test could catch this: nothing in the suite holds a competing handle
/// while a digest read runs, so every read looked fine in CI even though the very first
/// live handshake — computed while the game the agent is attached to is running — would
/// have silently downgraded TwoDa/Override/Appearance/Journal/Modules to "unknown" the
/// moment chitin.key (or a modded Override file) was actually locked the way swkotor.exe
/// locks it. These tests hold that exact handle shape (ReadWrite access, Read-only share —
/// the one combination a plain FileShare.Read reader cannot open, and FileShare.ReadWrite
/// can) and assert the real code paths still succeed.
/// </summary>
public sealed class GameFileSharingTests : IDisposable
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
    public void ChitinKeyReadsWhileHeldOpenTheWaySwkotorHoldsIt()
    {
        string gameDir = TempDirectory();
        string keyPath = Path.Combine(gameDir, "chitin.key");
        File.WriteAllBytes(keyPath, MinimalChitinKey());

        using FileStream heldOpen = SimulateEngineHandle(keyPath);

        // Sanity: this is a genuine sharing violation, not a no-op fixture. A plain
        // File.ReadAllBytes must fail against this handle, or the test proves nothing.
        Assert.Throws<IOException>(() => File.ReadAllBytes(keyPath));

        // The real code path: GameFiles' constructor loads chitin.key through KeyIndex.
        using GameFilesReader files = new(gameDir);
        Assert.True(files.IsUsable, "chitin.key should be readable even while the game holds it open");
    }

    [Fact]
    public void LooseOverrideFileReadsWhileHeldOpenTheWaySwkotorHoldsIt()
    {
        string gameDir = TempDirectory();
        string overrideDir = Path.Combine(gameDir, "Override");
        Directory.CreateDirectory(overrideDir);

        string loosePath = Path.Combine(overrideDir, "modtest.utw");
        byte[] content = "not real gff, just needs to round-trip"u8.ToArray();
        File.WriteAllBytes(loosePath, content);

        using FileStream heldOpen = SimulateEngineHandle(loosePath);

        Assert.Throws<IOException>(() => File.ReadAllBytes(loosePath));

        using GameFilesReader files = new(gameDir);
        Assert.True(files.TryReadResource("modtest", "utw", out byte[] data),
            "a loose Override file should be readable even while the game holds it open");
        Assert.Equal(content, data);
    }

    [Fact]
    public void InstallDigestHashesAModdedTwoDaWhileHeldOpenTheWaySwkotorHoldsIt()
    {
        string gameDir = TempDirectory();
        File.WriteAllBytes(Path.Combine(gameDir, "chitin.key"), MinimalChitinKey());

        string overrideDir = Path.Combine(gameDir, "Override");
        Directory.CreateDirectory(overrideDir);
        string twoDaPath = Path.Combine(overrideDir, "modtest.2da");
        File.WriteAllBytes(twoDaPath, "2DA V2.b\n\nlabel\n0 mod_row\n"u8.ToArray());

        using FileStream heldOpen = SimulateEngineHandle(twoDaPath);

        Assert.Throws<IOException>(() => File.ReadAllBytes(twoDaPath));

        ContentDigest digest = InstallDigest.Compute(gameDir, Path.Combine(gameDir, "swkotor.exe"));

        // Before the fix this field silently dropped to unknown the moment the game held
        // the file open — exactly the state a real live handshake would run in.
        Assert.True(digest.Knows(ContentDigest.Field.TwoDa),
            "the 2DA field should stay known even while a modded Override file is held open");
    }

    // ---- fixtures ----------------------------------------------------------------

    /// <summary>
    /// The one handle shape that is a sharing violation against a plain
    /// <c>File.ReadAllBytes</c> (Access=Read, Share=Read) reader but NOT against
    /// <c>GameFileIo</c>'s Access=Read, Share=ReadWrite reader: Access=ReadWrite (write
    /// access the "engine" never uses), Share=Read only. This is what the project's own
    /// comment on <c>GameFileIo.ReadSlice</c> says swkotor.exe does to its data files.
    /// </summary>
    private static FileStream SimulateEngineHandle(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

    private static byte[] MinimalChitinKey()
    {
        byte[] header = new byte[32];
        "KEY V1  "u8.ToArray().CopyTo(header, 0);
        // bifCount=0, keyCount=0 at 8/12 — the file/key tables are never walked, so the
        // offsets at 16/20 (and the reserved bytes) can be anything within the 32-byte
        // minimum the loader requires.
        return header;
    }

    private string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"kotorcoop-sharing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);
        return directory;
    }
}
