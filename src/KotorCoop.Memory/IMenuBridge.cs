// Shared pause/combat toggles, the chat overlay, and death/revive mediation.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IMenuBridge
{
    /// <summary>False when this bridge cannot mirror a peer's pause into the game.</summary>
    bool SupportsPauseSync => false;

    /// <summary>
    /// Holds (or releases) a pause on the peers' behalf. Either player pausing pauses
    /// both worlds — anything else lets one side act in a world the other believes is
    /// frozen.
    /// </summary>
    bool TrySetRemotePause(bool desired) => false;

    /// <summary>False when this bridge cannot replicate combat state into the game.</summary>
    bool SupportsCombatSync => false;

    /// <summary>
    /// Declares that a REMOTE peer standing in this player's module is fighting, so the
    /// plugin can put this engine into combat too. A client's own enemies are mirrors with
    /// the attack bit cleared, so its engine never works this out for itself — and combat
    /// state gates round timing, the HUD, auto-pause and how abilities queue.
    /// </summary>
    bool TrySetSessionInCombat(bool fighting) => false;

    /// <summary>The plugin's count of local releases of a MIRRORED pause. A change is the
    /// release; the absolute value means nothing.</summary>
    uint LocalPauseReleaseSerial => 0;

    /// <summary>Tells the plugin a peer ended the shared pause, so this instance drops the
    /// pause it started. Either player may end a shared pause.</summary>
    bool TrySignalRemotePauseRelease(uint serial) => false;

    /// <summary>Posts one line to the always-on chat overlay (used to surface a peer's
    /// dialogue). Default false when this bridge has no overlay.</summary>
    bool PostChatLine(string line) => false;

    /// <summary>A player chat line: shown on the overlay and kept in the Messages-screen inbox.</summary>
    bool PostInboxChatLine(string line) => PostChatLine(line);

    /// <summary>False when this bridge cannot mediate deaths (no plugin).</summary>
    bool SupportsDeathHandling => false;

    /// <summary>
    /// Holds back the two engine reflexes a co-op session must arbitrate itself: the
    /// game-over screen when this instance's whole party is down, and control auto-swapping
    /// off the player's own PC. Clearing <paramref name="suppressGameOver"/> releases a
    /// genuine, session-wide party wipe.
    /// </summary>
    bool SetDeathSuppression(bool suppressGameOver, bool suppressSwap) => false;

    /// <summary>True (once) when the engine tried to end the game and the plugin swallowed
    /// it — i.e. every party member on THIS instance is down.</summary>
    bool TryTakeGameOverAttempt() => false;

    /// <summary>Stands the local PC back up (a peer reached them). 0 = plugin default HP.</summary>
    bool RequestRevive(int hitPoints = 0) => false;
}
