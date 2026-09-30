using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    public void PublishNativePartyLoadProof(uint generation,uint serial,uint epoch,long requested,
        LocalSaveReadProof? proof)
    {
        byte* bank=baseAddress+SharedBlock.OffsetNativePartyLoadProof;
        *(uint*)bank=generation|1u;
        new Span<byte>(bank+4,SharedBlock.NativePartyLoadProofSize-4).Clear();
        *(uint*)(bank+4)=serial;*(uint*)(bank+8)=epoch;*(long*)(bank+216)=requested;
        if(proof is not null)
        {
            *(uint*)(bank+12)=1;*(long*)(bank+16)=proof.SaveLength;
            Convert.FromHexString(proof.SaveSha256).CopyTo(new Span<byte>(bank+24,32));
            Convert.FromHexString(proof.GlobalSha256).CopyTo(new Span<byte>(bank+56,32));
            Convert.FromHexString(proof.ManifestSha256).CopyTo(new Span<byte>(bank+88,32));
            Encoding.ASCII.GetBytes(proof.DirectoryLeaf,new Span<byte>(bank+120,96));
        }
        System.Threading.Volatile.Write(ref *(uint*)bank,generation);
    }
}
