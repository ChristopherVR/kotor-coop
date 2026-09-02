namespace KotorCoop.Session;

/// <summary>
/// Names a <c>STUNT_*</c> module — the scripted cutscene leg (~50 s, e.g. <c>STUNT_00</c>) the
/// engine runs between an interplanetary departure and the fresh module it lands in, and the
/// handful of other story beats that stage through the same mechanism. Inside one of these the
/// player's engine holds a CUTSCENE-ONLY WORLD: the party table is temporarily empty or
/// reshuffled, gold reads 0, the companions on screen are not the real roster, the running
/// "conversation" is a scripted cutscene rather than anything a peer could join, and interaction
/// locks name objects that will not exist a moment later. None of that is the shared-world
/// truth — it is local, never published, and never followed.
///
/// <see cref="KotorCoop.Session.Pumps.TransitionPump"/> already refuses to FOLLOW a peer into one
/// of these (the same prefix test, factored out here); this rule additionally gates the
/// state-publishing pumps (progression, party roster, the party pool, dialogue, interaction
/// locks, the store) so a host standing in a stunt module does not broadcast the fake world as if
/// it were real.
/// </summary>
public static class StuntModuleRule
{
    /// <summary>True when <paramref name="module"/> names a stunt/cutscene module — a prefix
    /// test, case-insensitive, matching the engine's own <c>STUNT_*</c> naming
    /// (<c>STUNT_00</c>, <c>stunt_16</c>, ...). False for <c>null</c>, empty, and any ordinary
    /// or fallback module name (e.g. the bridge's <c>areaXX</c> placeholder mid-transition).</summary>
    public static bool IsStuntModule(string? module) =>
        !string.IsNullOrEmpty(module) && module.StartsWith("stunt", StringComparison.OrdinalIgnoreCase);
}
