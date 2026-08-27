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
}
