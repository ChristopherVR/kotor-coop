namespace KotorCoop.Plugin;
internal sealed unsafe partial class SharedBlockView
{
    public uint LivenessExpectedWorldSerial => *U32(SharedBlock.OffsetLivenessExpectedWorldSerial);
    public uint LivenessExpectedEpoch => *U32(SharedBlock.OffsetLivenessExpectedEpoch);
    public uint LivenessPublishSequence => System.Threading.Volatile.Read(ref *U32(SharedBlock.OffsetLivenessPublishSequence));
    public uint LivenessNativeEpoch => *U32(SharedBlock.OffsetLivenessNativeEpoch);
    public void BeginLivenessEpoch()
    {
        LivenessCount = 0;
        *U32(SharedBlock.OffsetLivenessPublishSequence) = 0;
        uint next = LivenessNativeEpoch + 1;
        *U32(SharedBlock.OffsetLivenessNativeEpoch) = next == 0 ? 1 : next;
    }
}
