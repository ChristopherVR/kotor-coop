namespace KotorCoop.Session;

public static class NativePartyLoadProofRule
{
    public static bool Recent(long requested,long now)=>requested>=0 && now>=requested && now-requested<=90_000;
    public static bool Completed(uint requestedSerial,uint requestedEpoch,uint currentSerial,uint currentEpoch)=>
        currentSerial==unchecked(requestedSerial+1) && currentEpoch!=0 && currentEpoch!=requestedEpoch;
    /// <summary>A retained native pre-read can prove the current load even if the Agent
    /// attached afterward. Unknown/expired sources and later loads remain held.</summary>
    public static bool CanRestore(long requested,long now,uint requestedSerial,uint requestedEpoch,uint currentSerial,uint currentEpoch)=>
        Recent(requested,now) && Completed(requestedSerial,requestedEpoch,currentSerial,currentEpoch);
}
