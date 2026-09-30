using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KotorCoop.Protocol;

/// <summary>Local request-time byte proof, not a network packet or an inferred character identity.</summary>
public sealed record LocalSaveReadProof(string DirectoryLeaf,string SaveSha256,long SaveLength,
    string GlobalSha256,string ManifestSha256);

public static class LocalSaveReadProofReader
{
    public const string ManifestName="kotorcoop-party-provenance.json";
    private static readonly string[] Files=["SAVEGAME.sav","savenfo.res","PARTYTABLE.res","GLOBALVARS.res",ManifestName];
    private sealed record Stamp(long Length,long Written,long Created);
    public static bool ValidLeaf(string value) => value.Length is >=10 and <=95
        && value.AsSpan(0,6).IndexOfAnyExceptInRange('0','9')<0 && value.AsSpan(6,3).SequenceEqual(" - ")
        && value.AsSpan(9).IndexOfAny("\\/:.\0\r\n*?\"<>|")<0;
    public static bool TryResolve(string savesDirectory,int slot,LoadProvenanceSource source,string value,out string directory)
    {
        directory="";
        string leaf=source==LoadProvenanceSource.MainMenuDirectDirectory?value:
            source is LoadProvenanceSource.MainMenuSaveEntry or LoadProvenanceSource.ServerLoadRequest
                && slot is >=0 and <=999999?$"{slot:000000} - {value}":"";
        if(!ValidLeaf(leaf) || leaf.Any(c=>c is <' ' or >'~'))return false;
        directory=Path.Combine(Path.GetFullPath(savesDirectory),leaf);return true;
    }
    public static bool TryCapture(string directory,out LocalSaveReadProof? proof)
    {
        proof=null;
        try
        {
            string leaf=Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if(!ValidLeaf(leaf))return false;
            Stamp[] before=Stamps(directory);
            if(!Read(directory,out var first) || !before.SequenceEqual(Stamps(directory))
                || !Read(directory,out var second) || first!=second || !before.SequenceEqual(Stamps(directory)))return false;
            proof=first;return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or System.Security.SecurityException)
        {return false;}
    }
    private static Stamp[] Stamps(string directory)=>Files.Select(name=>
    {
        FileInfo file=new(Path.Combine(directory,name));
        if(!file.Exists)throw new FileNotFoundException(name);
        return new Stamp(file.Length,file.LastWriteTimeUtc.Ticks,file.CreationTimeUtc.Ticks);
    }).ToArray();
    private static bool Read(string directory,out LocalSaveReadProof proof)
    {
        proof=new("","",0,"","");
        using var save=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer=new byte[65536];Span<byte> lengthBytes=stackalloc byte[8];long total=0,saveLength=0;
        string globals="",manifest="";
        for(int i=0;i<Files.Length;i++)
        {
            using FileStream file=new(Path.Combine(directory,Files[i]),FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length is <=0 or >536_870_912 || (i==4 && file.Length>16384))return false;
            total=checked(total+file.Length);if(total>1_073_741_824)return false;
            using var separate=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if(i<3)
            {
                // Byte-identical to ClientCharacterPersistence.TryHashSave.
                save.AppendData(Encoding.ASCII.GetBytes(Files[i]));save.AppendData([1]);
                BinaryPrimitives.WriteInt64LittleEndian(lengthBytes,file.Length);save.AppendData(lengthBytes);
                saveLength=checked(saveLength+file.Length);
            }
            int count;while((count=file.Read(buffer))!=0)
            {if(i<3)save.AppendData(buffer.AsSpan(0,count));else separate.AppendData(buffer.AsSpan(0,count));}
            if(i==3)globals=Convert.ToHexString(separate.GetHashAndReset());
            if(i==4)manifest=Convert.ToHexString(separate.GetHashAndReset());
        }
        proof=new(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)),Convert.ToHexString(save.GetHashAndReset()),saveLength,globals,manifest);
        return true;
    }
}
