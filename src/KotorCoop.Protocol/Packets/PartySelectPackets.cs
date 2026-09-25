namespace KotorCoop.Protocol;

/// <summary>
/// client -&gt; host: what this player's own suppressed <c>CSWGuiPartySelection</c> panel
/// carried, plus the module it was raised in so a stale request (the sender has already moved
/// on) is ignored rather than acted on late.
///
/// 2026-09-25 second pass: <paramref name="TriggerId"/> — the trigger this player's own real
/// character most recently fired (correlated by <c>OdysseyTriggers.TryGetRecentLocalTrigger</c>,
/// 0 when nothing recent enough) — is what the host actually acts on now, by re-firing that
/// SAME trigger's OnEnter for its own player. <paramref name="ExitScript"/>/
/// <paramref name="ForceNpc1"/>/<paramref name="ForceNpc2"/> (the panel's own
/// <c>ShowPartySelectionGUI(string,int,int)</c> parameters) are relayed for DIAGNOSTICS ONLY —
/// a live pass found calling <c>ShowPartySelection</c> directly with them unreliable, because
/// the panel object is persistently reused and its fields can carry stale residue from an
/// earlier, unrelated call. See docs/research/galaxy-map-and-party-select.md's 2026-09-25
/// relay section (both the first pass and the "SECOND PASS" correction).
/// </summary>
public sealed record PartySelectRequestPacket(
    uint PeerId, uint RequestSerial, string Module, string ExitScript, int ForceNpc1, int ForceNpc2,
    uint TriggerId)
{
    public const int MaxScriptLength = 16;
}

/// <summary>What the host's own answer to a <see cref="PartySelectRequestPacket"/> was.</summary>
public enum PartySelectResponseStatus : byte
{
    None = 0,

    /// <summary>The host's own screen is open with the requested parameters.</summary>
    Opened = 1,

    /// <summary>The host confirmed a party — the screen closed and the roster changed. No
    /// further response follows for this request.</summary>
    Confirmed = 2,

    /// <summary>The host closed the screen without changing the party (the Back button, or the
    /// same party re-confirmed — see <c>PartySelectOutcomeCode.Cancelled</c>'s own doc comment
    /// for the one known false positive). No further response follows.</summary>
    Cancelled = 3,

    /// <summary>The host could not open the screen right now (busy, or the engine refused) —
    /// see the accompanying free-text <see cref="PartySelectResponsePacket.Detail"/>.</summary>
    Refused = 4,

    /// <summary>The host is already handling a different party-select request — debounce; ask
    /// again once the current one resolves.</summary>
    AlreadyPending = 5,
}

/// <summary>
/// host -&gt; client, unicast: answers a <see cref="PartySelectRequestPacket"/>.
/// <paramref name="ResponseSerial"/> is a per-request counter the host bumps for each DISTINCT
/// status it emits for the same <see cref="RequestSerial"/> (Opened, then later Confirmed or
/// Cancelled) — a receiver that keeps a "last handled ResponseSerial" per request can use it to
/// avoid reacting twice to a genuine resend, but nothing in this pass actually resends: each
/// status is currently sent exactly ONCE (no <c>AnnounceRepeats</c>-style redundancy), the same
/// "one-shot, caller's own timeout covers a loss" posture <see cref="PartySelectRequestPacket"/>
/// itself uses. A lost Opened is covered by the requester's own resend of the REQUEST (which the
/// debounce on the host side then answers again); a lost Confirmed/Cancelled has no such
/// coverage — the worst case is a stale "asked the host..." notice never getting a follow-up
/// line, while the module transition itself (on Confirmed) still reaches the joiner through the
/// ordinary transition-announce machinery regardless. Acceptable for this pass; a future one
/// could add repeats if this proves to matter live.
/// </summary>
public sealed record PartySelectResponsePacket(
    uint RequestSerial, uint ResponseSerial, PartySelectResponseStatus Status, string Detail)
{
    public const int MaxDetailLength = 48;
}
