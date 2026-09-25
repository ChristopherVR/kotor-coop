namespace KotorCoop.Session;

/// <summary>
/// The pure eligibility decision behind acting on an incoming
/// <see cref="KotorCoop.Protocol.PartySelectRequestPacket"/> — a peer's own suppressed
/// CSWGuiPartySelection screen, relayed here so this instance (which must be the host) opens
/// its own unmodified copy with the same parameters. docs/research/galaxy-map-and-party-select.md's
/// 2026-09-25 relay section.
/// </summary>
public static class PartySelectHostRequestRule
{
    /// <param name="isHost">Only the host ever opens a screen on a peer's behalf — a client
    /// receiving this packet (should never happen; the host is the only one anyone sends it
    /// to) ignores it.</param>
    /// <param name="sameModule">The requester's module (captured at the moment its own panel
    /// opened) equals THIS instance's CURRENT module. A request from a different module is a
    /// race with a transition already under way — cross-world/stale, per the task's own
    /// wording — and is not actionable here; the requester's own timeout covers it.</param>
    /// <param name="alreadyPending">A previous request is still awaiting the plugin's ack, or
    /// its screen is still open awaiting an outcome — debounce: only one party-select request
    /// is ever in flight on a host at a time.</param>
    public static bool ShouldAccept(bool isHost, bool sameModule, bool alreadyPending) =>
        isHost && sameModule && !alreadyPending;
}
