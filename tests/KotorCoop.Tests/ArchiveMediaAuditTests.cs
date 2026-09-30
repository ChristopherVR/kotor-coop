using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ArchiveMediaAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kotor-content-audit-" + Guid.NewGuid());
    public ArchiveMediaAuditTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void BaseArchiveEditsWithSameSizeAndTimestampChangeTheCompatibilityDigest()
    {
        string path = Write("data/test.bif", new byte[1024 * 1024]);
        WriteKey("data/test.bif");
        var a = ModContentManifest.Compute(_root);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 512 * 1024;
            stream.WriteByte(123); // Neither endpoint nor metadata changed.
        }
        File.SetLastWriteTimeUtc(path, modified);
        var b = ModContentManifest.Compute(_root);
        Assert.True(a.Complete);
        Assert.True(b.Complete);
        Assert.Equal(new[] { "data/test.bif" }, a.Differences(b));
        Assert.NotEqual(a.Digest(file => file.Gameplay), b.Digest(file => file.Gameplay));
        var left = new ContentDigest((ushort)ContentDigest.Field.Gameplay, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, GameplayDigest: a.Digest(file => file.Gameplay));
        var right = new ContentDigest((ushort)ContentDigest.Field.Gameplay, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, GameplayDigest: b.Digest(file => file.Gameplay));
        Assert.Contains(ContentCompatibility.Compare(left, right).Notes, note => note.Code == CompatibilityCode.Gameplay && note.IsDegradation);
    }

    [Theory]
    [InlineData("Movies/story.bik")]
    [InlineData("Override/script_voice.wav")]
    [InlineData("StreamMusic/finale.wav")]
    [InlineData("StreamSounds/trigger.wav")]
    [InlineData("StreamWaves/module/conversation/line.wav")]
    [InlineData("StreamVoice/module/line.mp3")]
    [InlineData("Lips/end_m01aa_loc.mod")]
    public void ScriptSelectedMediaIsHashedRecursivelyAndRemovalIsDetected(string relative)
    {
        string path = Write(relative, Encoding.ASCII.GetBytes("media A"));
        var a = ModContentManifest.Compute(_root);
        File.WriteAllText(path, "media B");
        var b = ModContentManifest.Compute(_root);
        Assert.Equal(new[] { relative.ToLowerInvariant() }, a.Differences(b));
        Assert.NotEqual(a.Digest(file => file.Gameplay), b.Digest(file => file.Gameplay));
        File.Delete(path);
        Assert.NotEqual(b.Digest(file => file.Gameplay), ModContentManifest.Compute(_root).Digest(file => file.Gameplay));
    }

    [Fact]
    public void IndexDefinedArchivesOutsideDataAreIncludedAndDuplicateReferencesAreHashedOnce()
    {
        Write("custom/base.bif", [1, 2, 3]);
        WriteKey("custom/base.bif", "custom/base.bif");
        var manifest = ModContentManifest.Compute(_root);
        Assert.True(manifest.Complete);
        Assert.Single(manifest.Files, file => file.Path == "custom/base.bif");
        Assert.Contains(manifest.Files, file => file.Path == "chitin.key" && file.Gameplay);
    }

    [Fact]
    public void MissingArchivesOrEscapingIndexPathsMakeAuditIncomplete()
    {
        WriteKey("data/missing.bif");
        Assert.False(ModContentManifest.Compute(_root).Complete);
        WriteKey("../outside.bif");
        var manifest = ModContentManifest.Compute(_root);
        Assert.False(manifest.Complete);
        Assert.Contains(manifest.Errors, error => error.Contains("escapes game directory"));
        Assert.DoesNotContain(manifest.Files, file => file.Path.Contains("outside"));
    }

    [Fact]
    public void MutableSavesLogsAndCoopBinariesDoNotChangeTheAudit()
    {
        Write("Movies/story.bik", [1]);
        var a = ModContentManifest.Compute(_root);
        foreach (string relative in new[] { "saves/slot/savegame.sav", "logs/run.log", "dinput8.dll", "coop.ini", "Movies/player.log", "StreamWaves/notes.txt", "modules/audit.log", "Override/coop.log", "Override/nativecoop.dll" })
            Write(relative, [7, 8]);
        var b = ModContentManifest.Compute(_root);
        Assert.Empty(a.Differences(b));
        Assert.Equal(a.Digest(file => file.Gameplay), b.Digest(file => file.Gameplay));
    }

    [Fact]
    public void DigestsAreStableAcrossCreationOrderAndInstallLocation()
    {
        Write("StreamWaves/z/last.wav", [1]);
        Write("StreamWaves/a/first.wav", [2]);
        var a = ModContentManifest.Compute(_root);
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(other, "StreamWaves", "a"));
        Directory.CreateDirectory(Path.Combine(other, "StreamWaves", "z"));
        File.WriteAllBytes(Path.Combine(other, "StreamWaves", "a", "first.wav"), [2]);
        File.WriteAllBytes(Path.Combine(other, "StreamWaves", "z", "last.wav"), [1]);
        var b = ModContentManifest.Compute(other);
        Assert.Empty(a.Differences(b));
        Assert.Equal(a.Digest(file => file.Gameplay), b.Digest(file => file.Gameplay));
    }

    private string Write(string relative, byte[] content)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private void WriteKey(params string[] archives)
    {
        byte[][] names = archives.Select(name => Encoding.ASCII.GetBytes(name + "\0")).ToArray();
        byte[] bytes = new byte[64 + 12 * names.Length + names.Sum(name => name.Length)];
        Encoding.ASCII.GetBytes("KEY V1  ").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)names.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 64);
        int cursor = 64 + 12 * names.Length;
        for (int i = 0; i < names.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64 + 12 * i + 4), (uint)cursor);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(64 + 12 * i + 8), (ushort)names[i].Length);
            names[i].CopyTo(bytes, cursor);
            cursor += names[i].Length;
        }
        Write("chitin.key", bytes);
    }
}
