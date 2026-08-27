namespace KotorCoop.Plugin;

/// <summary>
/// The pure decision behind the "&lt;name&gt; is in a minigame — waiting" HUD line (item 8
/// task 3): given what one remote slot's notice last showed, and this frame's raw
/// <see cref="RemoteView"/> for that slot, decide whether the notice should be showing now.
///
/// Kept as one static method, tested in isolation, rather than folded into
/// <see cref="MinigameNoticeController"/>'s per-frame loop — the same reasoning
/// <see cref="MinigameRule"/>'s own doc comment gives for its three gating questions: a wrong
/// verdict here is invisible until it is live, and this one has a real subtlety worth
/// pinning down with tests (see the ⚠ below).
///
/// ⚠ NOT SYMMETRIC WITH <c>Active</c>. While the slot is <c>Active</c> the flag is mirrored
/// exactly, every frame — that is the only state <see cref="RemoteView.InMinigame"/> can ever
/// be trusted for. While the slot is NOT <c>Active</c> (the peer's phantom has been destroyed,
/// or a peer that never had one connects mid-game), <see cref="RemoteView.Flags"/> is
/// deliberately left UNTOUCHED by <c>KotorGameBridge.WriteToPlugin</c> (it only zeroes the
/// Active dword for an excluded slot) — so the rule PRESERVES whatever the notice last showed
/// rather than trusting a stale read. See <see cref="MinigameNoticeController"/>'s own class
/// comment for the one architectural consequence this has: under the DEFAULT
/// <c>minigame_hide_peer=1</c>, a peer is filtered out of the write in the exact same tick
/// their flag turns true, so this rule's "preserve" branch will hold <c>false</c> forever for
/// that transition and the notice will not fire. That is not a bug in this rule — the rule is
/// doing the only thing that is SAFE given what the block can prove — it is a known
/// architectural gap documented for a future round.
/// </summary>
internal static class MinigameNoticeRule
{
    /// <param name="wasWaiting">Whether the notice was showing for this slot last frame.</param>
    /// <param name="active">This frame's <see cref="RemoteView.Active"/>.</param>
    /// <param name="peerInMinigame">This frame's <see cref="RemoteView.InMinigame"/> — only
    /// meaningful when <paramref name="active"/> is true; ignored otherwise.</param>
    public static bool NextWaiting(bool wasWaiting, bool active, bool peerInMinigame) =>
        active ? peerInMinigame : wasWaiting;
}
