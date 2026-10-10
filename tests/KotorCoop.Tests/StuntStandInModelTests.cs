using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The stand-in's sideways copy of a dialogue stunt track (STUNT_55a, 2026-10-10).</summary>
public sealed class StuntStandInModelTests
{
    [Fact]
    public void ThePluginAndTheAgentNameACopyTheSameWay()
    {
        foreach (string resref in new[] { "m44ac_c01_char02", "M44AC_C01_CHAR02", "sacrifice_char01" })
        {
            for (int variant = 0; variant < StuntStandInModels.Variants; variant++)
            {
                Assert.Equal(StuntStandInModels.NameFor(resref, variant), StuntStandInName.For(resref, variant));
            }
        }

        Assert.True(StuntStandInModels.NameFor("m44ac_c01_char02", 0).Length <= 16);
        Assert.NotEqual(StuntStandInModels.NameFor("m44ac_c01_char02", 0), StuntStandInModels.NameFor("m44ac_c01_char02", 1));
    }

    [Fact]
    public void TheCutsceneDummyPositionKeysMoveSideways()
    {
        byte[] model = TinyStuntModel(out uint valuesAt);
        Assert.True(StuntStandInModels.TryShift(model, "kcs0_12345678", 1.2f, 0f, out byte[] shifted));

        for (int k = 0; k < 2; k++)
        {
            float x = BinaryPrimitives.ReadSingleLittleEndian(shifted.AsSpan(12 + (int)valuesAt + (12 * k)));
            float y = BinaryPrimitives.ReadSingleLittleEndian(shifted.AsSpan(12 + (int)valuesAt + (12 * k) + 4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(shifted.AsSpan(12 + (int)valuesAt + (12 * k) + 8));
            Assert.Equal((10f * (k + 1)) + 1.2f, x, 3);
            Assert.Equal(5f, y, 3);
            Assert.Equal(1f, z, 3);
        }

        Assert.StartsWith("kcs0_12345678\0", Encoding.ASCII.GetString(shifted, 12 + 8, 32));
    }

    [Fact]
    public void AModelWithoutTheNodeIsLeftAlone()
    {
        byte[] model = TinyStuntModel(out _, nodeName: "rootdummy");
        Assert.False(StuntStandInModels.TryShift(model, "kcs0_12345678", 1.2f, 0f, out _));
    }

    [Fact]
    public void GarbageIsNotAModel()
    {
        Assert.False(StuntStandInModels.TryShift(new byte[64], "kcs0_12345678", 1.2f, 0f, out _));
    }

    /// <summary>
    /// The smallest model the shift walks: header, a names array (model name, the node), one
    /// animation whose root node carries a two-key position controller. Offsets are counted after
    /// the 12-byte file header, like the game's.
    /// </summary>
    private static byte[] TinyStuntModel(out uint valuesAt, string nodeName = "cutscenedummy")
    {
        byte[] body = new byte[1024];
        void U16(int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), v);
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), v);
        void F32(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(at), v);
        void Text(int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(body, at);

        Text(8, "m44ac_c01_char02");          // geometry name
        U32(88, 300); U32(92, 1);             // animations: array at 300, one
        U32(184, 320); U32(188, 2);           // names: array at 320, two
        U32(320, 340); U32(324, 360);
        Text(340, "M44ac_c01_char02");
        Text(360, nodeName);
        U32(300, 400);                        // the animation
        U32(400 + 40, 500);                   // its root node
        Text(400 + 88, "M44ac_c01_char02");
        U16(500 + 4, 1);                      // node name index 1
        U32(500 + 56, 600); U32(500 + 60, 1); // one controller at 600
        U32(500 + 68, 700);                   // data at 700
        U32(600, 8);                          // position
        U16(600 + 6, 2);                      // two rows
        U16(600 + 8, 0);                      // times at 0
        U16(600 + 10, 2);                     // values at float 2
        body[600 + 12] = 3;                   // three columns
        F32(700, 0f); F32(704, 1f);           // times
        valuesAt = 708;
        F32(708, 10f); F32(712, 5f); F32(716, 1f);
        F32(720, 20f); F32(724, 5f); F32(728, 1f);

        byte[] file = new byte[12 + body.Length];
        body.CopyTo(file, 12);
        return file;
    }
}
