namespace KotorCoop.Plugin;

/// <summary>What a party-drive pass should do this time round, given the roster freeze edge.</summary>
internal enum PartyFreezeAction
{
    /// <summary>Not frozen, and was not frozen last pass either: drive normally.</summary>
    Drive,

    /// <summary>The freeze just started. Stop claiming and driving, release the faction/health
    /// hold on whatever this instance was holding, and say so once.</summary>
    ReleaseAndLog,

    /// <summary>Still frozen since the last pass: stay off, silently — the edge was already
    /// reported and reporting it every frame would be a log line eight times a second for
    /// however long the engine's sequence runs.</summary>
    StayOff,

    /// <summary>The freeze just lifted. Resume driving THIS pass — there is nothing left to
    /// release, because the freezing edge already handed everything back — and say so once.</summary>
    ResumeAndLog,
}

/// <summary>
/// The decision half of the roster-freeze guard in <c>PartyPresenceController</c>, separated so
/// it can be unit-tested — the guard's other half is <c>Odyssey.IsPartyRosterFrozen()</c> and the
/// engine calls that release a companion, neither of which exists in the test runner (the same
/// split <c>ControlReturnRule</c> and <c>SpawnDrainRule</c> use).
///
/// The question it answers, once per pass: the engine freezes the WHOLE roster (party table
/// +0xF0 != -1) while a script has taken one specific companion under direct control — a
/// Leviathan brig beat, the Rakatan temple, any forced-solo sequence. This controller does not
/// touch the roster, but it drives EVERY companion body in the party every pass, and one of them
/// may be exactly the body the script now owns. Pinning its health and re-claiming it out from
/// under a running script is not a gap this codebase has ever chosen to leave open elsewhere
/// (see <c>PartyRosterController</c>'s identical guard on the roster side), so the presence
/// mirror has to stop too, for as long as the freeze lasts.
/// </summary>
internal static class PartyFreezeRule
{
    /// <summary>
    /// Decides a pass from the freeze reading alone. Edge-triggered on both sides so the log
    /// carries exactly two lines per freeze — one when it starts, one when it ends — rather than
    /// one per frame for however long a scripted sequence runs.
    /// </summary>
    public static PartyFreezeAction Decide(bool frozenNow, bool frozenBefore)
    {
        if (frozenNow)
        {
            return frozenBefore ? PartyFreezeAction.StayOff : PartyFreezeAction.ReleaseAndLog;
        }

        return frozenBefore ? PartyFreezeAction.ResumeAndLog : PartyFreezeAction.Drive;
    }
}
