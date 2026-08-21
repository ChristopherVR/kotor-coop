namespace KotorCoop.Plugin;

/// <summary>
/// The pure identity check <see cref="PartyPresenceController.Resolve"/> applies to a
/// candidate companion body: id AND vtable must both still match. Pulled out into its own
/// pure, dependency-free function so the RULE — "identity is the vtable, never the
/// <c>+0x08</c> type field" — can be regression-tested without a live game process, the same
/// discipline <c>MirrorHealthRule</c>/<c>ControlReturnRule</c>/etc already hold.
///
/// ⚠ ROOT CAUSE (party-presence staleness, docs/research/party-reconciliation.md, dated entry
/// 2026-08-22, "Live pass"). <see cref="PartyPresenceController.Resolve"/> used to compare
/// <c>CGameObject+0x08 == 5</c> — the "object type" field this codebase's own
/// object-type-field-trap lesson already proved carries extra flag bits: a live, running
/// creature routinely reads something like <c>0x40005</c>, not the bare enum value <c>5</c>.
/// That exact comparison intermittently rejected a perfectly live, perfectly resolvable
/// companion, and <c>Resolve</c> returning 0 makes <c>DriveOne</c> bail out before it ever
/// reaches <c>MotionSmoother.Step</c> — freezing the mirrored companion in place, sometimes for
/// minutes, while every OTHER channel (enemies, the phantom) kept moving normally, because
/// <see cref="EnemyPresenceController"/> was never bitten by this: it has always identified a
/// creature by its vtable (<see cref="CreatureVtable"/>), never by the flag-bearing type field.
/// Disconnect/reconnect "fixed" it only by coincidence — <see cref="PartyPresenceController.
/// Invalidate"/> forces a fresh <see cref="PartyPresenceController.Resolve"/> on the next pass,
/// which only succeeds once the transient flag bits at <c>+0x08</c> happen to have cleared,
/// which they eventually do (combat ending, an AI state changing) — not because reconnecting
/// does anything to the creature itself.
/// </summary>
public static class PartyCreatureIdentity
{
    /// <summary>Server-side <c>CSWSCreature</c> vtable — the same constant
    /// <see cref="EnemyPresenceController"/> validates a mirrored enemy against.</summary>
    public const nint CreatureVtable = 0x007470C8;

    /// <summary>
    /// True when a candidate object is still the SAME live creature this controller wants:
    /// its id (read at <c>+0x04</c> by the caller) still matches, and its vtable (read at
    /// <c>+0x00</c> by the caller) is a server creature's. Deliberately never looks at
    /// <c>+0x08</c> — see the class comment.
    /// </summary>
    public static bool IsLiveMatch(nint candidateVtable, uint candidateId, uint expectedId) =>
        candidateVtable == CreatureVtable && candidateId == expectedId;
}
