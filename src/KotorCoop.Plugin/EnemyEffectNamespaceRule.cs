namespace KotorCoop.Plugin;
internal static class EnemyEffectNamespaceRule
{
    public static bool Matches(uint sequence, uint afterSequence, uint expectedSession, uint activeSession,
        uint expectedEpoch, uint actualEpoch, uint expectedLoad, uint actualLoad) => sequence != 0
        && (sequence & 1) == 0 && sequence == afterSequence && expectedSession != 0
        && expectedSession == activeSession && expectedEpoch != 0 && expectedEpoch == actualEpoch && expectedLoad == actualLoad;
}
