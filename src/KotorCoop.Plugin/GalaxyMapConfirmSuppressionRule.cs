namespace KotorCoop.Plugin;

/// <summary>
/// The pure decision behind galaxy-map gating (Tier 2 item 18, task 2's plugin-side follow-up;
/// coop.ini <c>galaxy_map_host_only</c>, plugin key, default ON, NEEDS-LIVE): should THIS
/// instance refuse its own click on <c>CSWGuiInGameGalaxyMap</c>'s accept button right now,
/// rather than let it run the confirm script and start the departure?
///
/// Mirrors <c>KotorCoop.Session.GalaxyMapConfirmRefusalRule.ShouldRefuse(isClient, mode)</c>
/// (docs/research/galaxy-map-and-party-select.md, "Galaxy-map gating" section) EXACTLY in
/// decision shape — refuse only a CLIENT, and only under <c>follow_lead=host</c> — but restated
/// here with plain booleans instead of that class's own <c>FollowLeadMode</c> enum, because the
/// plugin is NativeAOT x86 and cannot reference <c>KotorCoop.Session</c>'s assembly (the same
/// constraint <see cref="PluginConfig"/>'s own doc comment gives for why it is a private ini
/// reader rather than a shared one). <paramref name="followLeadHost"/> is this instance's own
/// direct read of coop.ini <c>follow_lead</c> (<c>== "host"</c>, case-insensitive; the default,
/// absent the key, is <c>any</c> — the identical default <c>CoopConfig.cs</c>'s own parse uses),
/// not anything armed by the agent — unlike <c>ClientDoorRefusalRule</c>'s door case, this
/// feature needs no <see cref="SharedBlock"/> field or session-side role computation, for the
/// same reason <see cref="PartySelectSuppressionRule"/>'s own doc comment gives: the plugin
/// already has everything it needs in-process.
///
/// <paramref name="isClientInLiveSession"/> is meant to be
/// <c>SharedBlockView.TryGetHostPartyRoster</c>'s own valid bit — the SAME "am I a client with a
/// live host roster right now" signal <see cref="PartySelectSuppressionRule"/> already reuses
/// for an identical purpose.
///
/// Deliberately does NOT reach for <c>ClientDoorRefusalRule</c>'s extra "is the host busy right
/// now" gate under <c>follow_lead=any</c> — see the session-side rule's own doc comment for why:
/// confirming a planet is not itself the moment the host would have to interrupt anything, it
/// only starts the same announce-then-follow chain an ordinary door crossing already uses, so
/// the host's own busy-ness is checked once, at the point it would actually follow, not a
/// second time at confirm. So under <c>follow_lead=any</c> this always returns false.
/// </summary>
internal static class GalaxyMapConfirmSuppressionRule
{
    /// <param name="enabled">coop.ini <c>galaxy_map_host_only</c>.</param>
    /// <param name="isClientInLiveSession">See the type's own doc comment.</param>
    /// <param name="followLeadHost">coop.ini <c>follow_lead == "host"</c> (this instance's own
    /// direct read; <c>false</c> means <c>any</c>, the default).</param>
    public static bool ShouldRefuse(bool enabled, bool isClientInLiveSession, bool followLeadHost) =>
        enabled && isClientInLiveSession && followLeadHost;
}
