namespace KotorCoop.Plugin;

internal static class PausePresentationRule
{
    // CGuiInGame::SetPaused / CSWGuiPause::SetPauseReason map reason 1 to
    // ENEMY SIGHTED. Manual pauses use 4; menus, dialogue and scripts keep their paths.
    public static bool SuppressEnemyBanner(bool coopKeepAlive, int paused, int reason)
        => coopKeepAlive && paused != 0 && reason == 1;

    // These screens intentionally manage their own timers. An absent player-pause
    // mask is not evidence that their stopped timer is stray.
    public static bool CanReconcile(bool fullScreenGui, bool conversation, bool textModal)
        => !fullScreenGui && !conversation && !textModal;

    // Save/menu return can restore the panel's intent without restoring any pause.
    // Require both running timers and no GUI pause belief or mask: a real player,
    // peer or script pause must never be converted into an unpause here.
    public static bool ClearOrphanedPanel(bool coopKeepAlive, int mask, bool panelIntent,
        bool guiBelievesPaused, bool clientTimerPaused, bool serverTimerPaused)
        => coopKeepAlive && mask == 0 && panelIntent && !guiBelievesPaused
            && !clientTimerPaused && !serverTimerPaused;
}
