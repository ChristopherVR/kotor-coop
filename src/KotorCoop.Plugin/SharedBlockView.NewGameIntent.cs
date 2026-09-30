namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    public uint NewGameIntentGeneration => *U32(SharedBlock.OffsetNewGameIntent);
    public uint MenuLoadNewGameGeneration => *U32(SharedBlock.OffsetMenuLoadNewGameGeneration);
    public void PublishNewGameIntent(long requestedAtTick)
    {
        byte* bank=Base+SharedBlock.OffsetNewGameIntent;
        uint next=unchecked(*(uint*)bank+2); if(next==0)next=2;
        *(uint*)bank=next-1;
        *(uint*)(bank+4)=WorldLoadSerial; *(uint*)(bank+8)=LivenessNativeEpoch;
        *(long*)(bank+16)=requestedAtTick;
        System.Threading.Thread.MemoryBarrier(); *(uint*)bank=next;
    }
}
