using System.Buffers.Binary;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decode behind tools\crashbin.ps1's C# twin: turning the raw bytes
/// CrashProbe.BuildHandler's machine code writes (and kernel32!WriteFile persists to
/// kotorcoop-crash.bin) back into fields, without a live process, a game, or even CrashProbe
/// itself — mirrors LogRotationTests' relationship to LogRotation.
/// </summary>
public class CrashRecordTests
{
    private static byte[] BuildRecord(uint serial, uint code, uint address, uint eip, uint esp, uint[] stack)
    {
        byte[] bytes = new byte[CrashRecord.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x00), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x04), code);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x08), address);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x0C), eip);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10), esp);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x14), (uint)stack.Length);
        for (int i = 0; i < stack.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CrashRecord.HeaderSize + (i * 4)), stack[i]);
        }

        return bytes;
    }

    [Fact]
    public void DecodesEveryHeaderField()
    {
        byte[] bytes = BuildRecord(serial: 3, code: CrashRecord.AccessViolation, address: 0x77123456,
            eip: 0x00412345, esp: 0x0012FF00, stack: [0x00401000, 0x00412300]);

        CrashRecord.Report? report = CrashRecord.Decode(bytes);

        Assert.NotNull(report);
        Assert.Equal(3u, report!.Value.Serial);
        Assert.Equal(CrashRecord.AccessViolation, report.Value.Code);
        Assert.Equal(0x77123456u, report.Value.Address);
        Assert.Equal(0x00412345u, report.Value.Eip);
        Assert.Equal(0x0012FF00u, report.Value.Esp);
        Assert.Equal([0x00401000u, 0x00412300u], report.Value.Stack);
    }

    [Fact]
    public void TooShortASpanDecodesToNull()
    {
        // The file caught mid-write by CREATE_ALWAYS truncating it, or a session that never
        // faulted leaving a zero-length .bin — either way "nothing to show", not a throw.
        Assert.Null(CrashRecord.Decode(new byte[CrashRecord.Size - 1]));
        Assert.Null(CrashRecord.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ExtraTrailingBytesAreIgnored()
    {
        // RecordCount's whole premise: Decode only ever looks at the first Size bytes, so a
        // longer buffer (a second record, or slack past EOF) does not confuse it.
        byte[] one = BuildRecord(1, CrashRecord.FailFast, 0x1, 0x2, 0x3, []);
        byte[] padded = new byte[CrashRecord.Size + 40];
        one.CopyTo(padded, 0);

        CrashRecord.Report? report = CrashRecord.Decode(padded);

        Assert.NotNull(report);
        Assert.Equal(1u, report!.Value.Serial);
    }

    [Fact]
    public void StackWordCountIsClampedToCapacity()
    {
        // A corrupt or torn write must not read past the header's own 64-word capacity, the
        // same clamp CrashProbe.DrainReports applies to the in-memory record.
        byte[] bytes = BuildRecord(1, CrashRecord.AccessViolation, 0, 0, 0, []);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x14), 0xFFFFFFFF);

        CrashRecord.Report? report = CrashRecord.Decode(bytes);

        Assert.NotNull(report);
        Assert.Equal(CrashRecord.StackWords, report!.Value.Stack.Length);
    }

    [Fact]
    public void StackWordCountAtZeroDecodesAnEmptyStack()
    {
        byte[] bytes = BuildRecord(1, CrashRecord.AccessViolation, 0, 0, 0, []);

        CrashRecord.Report? report = CrashRecord.Decode(bytes);

        Assert.NotNull(report);
        Assert.Empty(report!.Value.Stack);
    }

    [Theory]
    [InlineData(CrashRecord.AccessViolation, "access violation")]
    [InlineData(CrashRecord.FailFast, "FAIL-FAST (0xC0000602)")]
    [InlineData(CrashRecord.StackOverflow, "stack overflow")]
    [InlineData(0x12345678u, "exception")]
    public void ExceptionNameMatchesWhatDrainReportsWouldPrint(uint code, string expected)
    {
        Assert.Equal(expected, CrashRecord.ExceptionName(code));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(280, 1)]
    [InlineData(559, 1)]
    [InlineData(560, 2)]
    public void RecordCountDividesFileLengthBySize(long fileLength, int expected)
    {
        Assert.Equal(CrashRecord.Size, 280);
        Assert.Equal(expected, CrashRecord.RecordCount(fileLength));
    }
}
