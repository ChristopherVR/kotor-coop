using System.Reflection;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Guards the PacketCodec.*.cs split (one partial file per protocol feature): every
/// <see cref="PacketType"/> the wire actually uses must still have both a
/// <c>Read&lt;TypeName&gt;</c> method and a matching <c>Write(Span&lt;byte&gt;, TPacket)</c>
/// overload somewhere in the (now multi-file) <see cref="PacketCodec"/> partial class, found
/// purely by reflection so a future split/rename that drops one half is caught even though the
/// individual round-trip tests only ever exercise the pairs they already know about.
/// </summary>
public class PacketCodecCoverageTests
{
    [Fact]
    public void EveryPacketTypeHasAMatchingReadAndWriteMethod()
    {
        MethodInfo[] methods = typeof(PacketCodec).GetMethods(BindingFlags.Public | BindingFlags.Static);

        HashSet<Type> writtenPacketTypes = methods
            .Where(m => m.Name == "Write" && m.GetParameters().Length == 2)
            .Select(m => m.GetParameters()[1].ParameterType)
            .ToHashSet();

        foreach (PacketType type in Enum.GetValues<PacketType>())
        {
            if (type == PacketType.None)
            {
                continue; // not a real wire packet -- PeekType's own "nothing here" answer
            }

            MethodInfo? readMethod = typeof(PacketCodec).GetMethod(
                $"Read{type}", BindingFlags.Public | BindingFlags.Static);
            Assert.True(readMethod is not null, $"PacketCodec has no Read{type}(ReadOnlySpan<byte>) method");

            Type packetType = readMethod!.ReturnType;
            Assert.True(
                writtenPacketTypes.Contains(packetType),
                $"PacketCodec has Read{type} returning {packetType.Name}, but no matching " +
                $"Write(Span<byte>, {packetType.Name}) overload");
        }
    }
}
