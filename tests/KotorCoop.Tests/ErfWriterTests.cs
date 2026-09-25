using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ErfWriter.ReplaceMember"/>: the one write primitive
/// docs/research/personal-inventory.md's save-file transform needs and this codebase did not
/// have — rewriting ONE member of an ERF/SAV archive while leaving every other member's bytes
/// and the key table's resref/type/order untouched.
/// </summary>
public sealed class ErfWriterTests
{
    [Fact]
    public void ReplacesTheTargetMemberAndLeavesEveryOtherMemberByteIdentical()
    {
        byte[] first = Encoding.ASCII.GetBytes("first-member-bytes");
        byte[] second = Encoding.ASCII.GetBytes("second");
        byte[] third = Encoding.ASCII.GetBytes("a rather longer third member, on purpose");

        byte[] erf = BuildErf(
            ("AVAILNPC0", 2027 /* utc */, first),
            ("INVENTORY", 0 /* res */, second),
            ("REPUTE", 2038 /* fac */, third));

        byte[] replacement = Encoding.ASCII.GetBytes("a brand-new, much bigger inventory blob than before");
        byte[] updated = ErfWriter.ReplaceMember(erf, "INVENTORY", 0, replacement);

        Assert.True(ErfArchive.TryFindMember(updated, 2027, "AVAILNPC0", out byte[] readFirst));
        Assert.Equal(first, readFirst);

        Assert.True(ErfArchive.TryFindMember(updated, 0, "INVENTORY", out byte[] readReplaced));
        Assert.Equal(replacement, readReplaced);

        Assert.True(ErfArchive.TryFindMember(updated, 2038, "REPUTE", out byte[] readThird));
        Assert.Equal(third, readThird);
    }

    [Fact]
    public void AShrunkMemberAlsoLeavesLaterMembersReadable()
    {
        byte[] erf = BuildErf(
            ("INVENTORY", 0, Encoding.ASCII.GetBytes("a long original inventory blob, much longer than the replacement")),
            ("AFTER", 2038, Encoding.ASCII.GetBytes("the member that comes after it")));

        byte[] updated = ErfWriter.ReplaceMember(erf, "INVENTORY", 0, Encoding.ASCII.GetBytes("short"));

        Assert.True(ErfArchive.TryFindMember(updated, 0, "INVENTORY", out byte[] readInv));
        Assert.Equal("short", Encoding.ASCII.GetString(readInv));

        Assert.True(ErfArchive.TryFindMember(updated, 2038, "AFTER", out byte[] readAfter));
        Assert.Equal("the member that comes after it", Encoding.ASCII.GetString(readAfter));
    }

    [Fact]
    public void MatchIsCaseInsensitiveOnTheResref()
    {
        byte[] erf = BuildErf(("Inventory", 0, Encoding.ASCII.GetBytes("original")));
        byte[] updated = ErfWriter.ReplaceMember(erf, "INVENTORY", 0, Encoding.ASCII.GetBytes("replaced"));

        Assert.True(ErfArchive.TryFindMember(updated, 0, "inventory", out byte[] read));
        Assert.Equal("replaced", Encoding.ASCII.GetString(read));
    }

    [Fact]
    public void AMissingMemberThrows()
    {
        byte[] erf = BuildErf(("SOMETHINGELSE", 0, [1, 2, 3]));
        Assert.Throws<InvalidDataException>(
            () => ErfWriter.ReplaceMember(erf, "INVENTORY", 0, [9]));
    }

    [Fact]
    public void ATypeMismatchOnTheSameResrefStillCountsAsMissing()
    {
        // "INVENTORY" under a DIFFERENT resource type is a different member, not a match —
        // the same resref+type pairing ErfArchive.TryFindMember itself keys on.
        byte[] erf = BuildErf(("INVENTORY", 2027 /* utc, not res */, [1, 2, 3]));
        Assert.Throws<InvalidDataException>(
            () => ErfWriter.ReplaceMember(erf, "INVENTORY", 0, [9]));
    }

    [Fact]
    public void TooShortToBeAnErfThrows() =>
        Assert.Throws<InvalidDataException>(() => ErfWriter.ReplaceMember([1, 2, 3], "X", 0, [9]));

    /// <summary>Lays out an ERF exactly as <see cref="ErfArchive"/> decodes it — mirrors
    /// WorldAdoptionTests's own private helper of the same shape.</summary>
    private static byte[] BuildErf(params (string Resref, int Type, byte[] Data)[] members)
    {
        int count = members.Length;
        int keysOffset = 160;
        int valuesOffset = keysOffset + (24 * count);
        int dataOffset = valuesOffset + (8 * count);

        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes("SAV ").CopyTo(file, 0);
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
