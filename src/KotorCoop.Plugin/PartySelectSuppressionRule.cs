namespace KotorCoop.Plugin;

/// <summary>
/// The pure decision behind party-select suppression on the client (Tier 2 item 18, second
/// half, task 1; coop.ini <c>party_select_host_only</c>, plugin key, default ON, NEEDS-LIVE):
/// should THIS instance tear down its own copy of the retail party-selection screen
/// (<c>CSWGuiPartySelection</c>) the moment it opens, rather than let the player pick?
///
/// docs/research/galaxy-map-and-party-select.md §2.4: the host is already the sole authority
/// for every other roster change in this codebase — <see cref="PartyRosterController"/>'s poll
/// converges a client's roster from the host's published masks regardless of what changed
/// them (a conversation, the party-select screen, anything) — so a client never gets a real
/// choice here by design; there is exactly one roster.
///
/// <paramref name="isClientInLiveSession"/> is meant to be
/// <c>SharedBlockView.TryGetHostPartyRoster</c>'s own valid bit — the SAME "am I a client with
/// a live host roster right now" signal <c>PartyRosterController.SyncClientPartyTogetherState</c>
/// already uses for an identical purpose (that method's own doc comment calls it "the cleanest
/// ... signal this plugin has - it is only ever written for a client"). Reusing it here means
/// this feature needs no new SharedBlock field, no session-side role computation, and no
/// ITravelBridge-style arm channel: the plugin already has everything it needs to answer this
/// question on its own, in-process, exactly like every other rule in this file's neighbourhood
/// that is deliberately kept separate from its controller so a wrong verdict — invisible until
/// it is live — can be pinned down with tests first.
/// </summary>
internal static class PartySelectSuppressionRule
{
    /// <param name="enabled">coop.ini <c>party_select_host_only</c>.</param>
    /// <param name="isClientInLiveSession">See the type's own doc comment.</param>
    public static bool ShouldSuppress(bool enabled, bool isClientInLiveSession) =>
        enabled && isClientInLiveSession;

    /// <summary>
    /// Whether a captured panel should ALSO be relayed to the host as a request (2026-09-25),
    /// rather than just silently suppressed. Relay only ever makes sense as a REFINEMENT of
    /// suppression — a client whose screen is not suppressed in the first place already gets
    /// to pick locally (desync risk aside, that is a different feature entirely) — so this
    /// requires <paramref name="hostOnlyEnabled"/> too, not just its own key.
    /// </summary>
    /// <param name="hostOnlyEnabled">coop.ini <c>party_select_host_only</c>.</param>
    /// <param name="relayEnabled">coop.ini <c>party_select_relay</c> (default ON).</param>
    /// <param name="isClientInLiveSession">See the type's own doc comment.</param>
    public static bool ShouldRelay(bool hostOnlyEnabled, bool relayEnabled, bool isClientInLiveSession) =>
        hostOnlyEnabled && relayEnabled && isClientInLiveSession;

    /// <summary>
    /// Live pass 2026-08-28 (docs/research/galaxy-map-and-party-select.md's dated section):
    /// <c>CGuiInGame+0x78</c> reads as a live, vtable-matched <c>CSWGuiPartySelection*</c> from
    /// the very first frame after a fresh save load and NEVER goes null again — 70,000+
    /// suppress/sweep pairs logged on a client in under 8 minutes, roughly 500/second, far
    /// faster than the render frame rate, meaning the controller is re-detecting and
    /// re-tearing-down the IDENTICAL address over and over rather than a freshly (re)opened
    /// screen. Two readings were possible when that finding was made; this rule commits to the
    /// SAFE one regardless of which is true: whether the address is genuinely dangling (our own
    /// sweep-teardown frees the object but nothing ever nulls <c>CGuiInGame+0x78</c> back to 0,
    /// so the next frame's raw pointer walk reads freed memory whose vtable header has not yet
    /// been overwritten) or a persistent CGuiInGame member the retail engine never intended
    /// individually deleted this way, blindly writing <c>panel+0x44</c> through the SAME address
    /// hundreds of times a second is never correct — at best it is a no-op repeated for no
    /// reason, at worst it is a write through a stale pointer into whatever heap block has since
    /// been reallocated there (a real risk: a module load, which is exactly when the STUNT_00
    /// crash this finding was investigating fires, churns the heap harder than almost anything
    /// else in the game).
    ///
    /// The fix that costs nothing and cannot make the feature worse: touch a given panel address
    /// AT MOST ONCE per module/session (the address is remembered until <c>Invalidate()</c>
    /// clears it on a module change or session end, the same boundary every other cached pointer
    /// in this controller already resets on). A GENUINE new screen opening after a module change
    /// still gets suppressed exactly as before; the runaway re-detection of the identical stale
    /// address does not.
    /// </summary>
    /// <param name="candidate">The panel address <c>GetPartySelectionPanel()</c> just found.</param>
    /// <param name="lastHandled">The last panel address this controller already tore down this
    /// module/session, or 0 if none yet.</param>
    public static bool IsAlreadyHandled(nint candidate, nint lastHandled) =>
        lastHandled != 0 && candidate == lastHandled;
}
