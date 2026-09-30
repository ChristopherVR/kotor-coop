using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>Completion is not a request acknowledgement: all required files must parse.</summary>
public static class NativeSaveArtifactRule
{
    public static bool TryHashOutputs(string directory,out string hash)
    {
        hash="";
        try
        {
            using var digest=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            byte[] buffer=new byte[65536];
            foreach(string name in new[]{"SAVEGAME.sav","savenfo.res","PARTYTABLE.res","GLOBALVARS.res"})
            {
                using FileStream file=new(Path.Combine(directory,name),FileMode.Open,FileAccess.Read,FileShare.Read);
                if(file.Length is <=0 or >536_870_912)return false;
                digest.AppendData(BitConverter.GetBytes(file.Length));
                int count;while((count=file.Read(buffer))!=0)digest.AppendData(buffer.AsSpan(0,count));
            }
            hash=Convert.ToHexString(digest.GetHashAndReset());return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException){return false;}
    }
    public static bool Complete(string directory)
    {
        try
        {
            foreach(string name in new[]{"savenfo.res","PARTYTABLE.res","GLOBALVARS.res"})
            {
                string path=Path.Combine(directory,name);
                if(!File.Exists(path) || new FileInfo(path).Length is <56 or >16_777_216)return false;
                Gff.Parse(File.ReadAllBytes(path));
            }
            // Includes native archive framing, nested current module and its IFO/GIT.
            return SaveDigest.TryReadActiveModuleName(directory,out string module) && module.Length>0
                && SaveDigest.TryComputeSaveIdentity(directory,out uint identity) && identity!=0;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
        { return false; }
    }
}
