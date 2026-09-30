namespace KotorCoop.Session;

internal static class LivenessAdmissionRule
{
    public static bool IsNewer(uint candidate, uint previous) => unchecked((int)(candidate - previous)) > 0;
    public static bool Accept(uint packetSession, uint expectedSession, uint packetLoad, uint expectedLoad,
        bool worldKnown, bool quarantined, uint generation, uint previousGeneration, bool sameCycleNamespace) =>
        !quarantined && worldKnown && expectedSession != 0 && packetSession == expectedSession
        && packetLoad == expectedLoad
        && (!sameCycleNamespace || generation == previousGeneration || IsNewer(generation, previousGeneration));
}
