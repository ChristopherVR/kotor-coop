/// <summary>
/// What the agent loop needs from a session, whether it is hosting or joining.
/// Deliberately poll-based: a game loop wants to ask "where is everyone right now",
/// not to be called back on a socket thread.
///
/// Split into role interfaces (one per concern, each carrying its own default
/// implementations) so a session or a test fake can be read one concern at a time; this file
/// is just the union, mirroring <c>IGameBridge</c> in KotorCoop.Memory.
/// </summary>
namespace KotorCoop.Session;

public interface ICoopSession :
    IPeerSession,
    ILifecycleSession,
    IFlagSession,
    IItemSession,
    IEnemySession,
    IDamageSession,
    ITriggerSession,
    IBarkSession,
    IMovieSession,
    ICapabilitySession,
    IPartySession,
    IDialogueSession,
    IClassGrantSession,
    IPlacedMineSession,
    IEnemyEffectSession,
    IInteractionLockSession,
    IChatSession,
    ILootSession,
    IJournalSession,
    IPartyPoolSession,
    IStoreSession,
    ICreditGrantSession,
    IPartySelectSession,
    IDisposable
{
}
