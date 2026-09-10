namespace KotorCoop.Plugin;

/// <summary>
/// Repairs a first-time joiner's starting gear AFTER their `join_mode = newcharacter` adoption
/// finishes loading — not before, which is where the original (2026-09-10 round 1-3) design put
/// it and where round 4's own live diagnostic proved it can never work.
///
/// ROUND 4's CORRECTED UNDERSTANDING (docs/research/world-adoption.md, "Round 2026-09-10e"):
/// rounds 1-3 read <see cref="Odyssey.GetPlayerObjectId"/>'s <c>0x7FFFFFFF</c> return as "no real
/// player registered yet, wait for a real one" and spent three rounds hunting for when a small,
/// dynamic-pool-looking id (like the <c>0x120</c>s a companion or an item gets) would eventually
/// replace it. Round 4 pushed the equip-repair's own wall-clock ceiling to 150 SECONDS and
/// captured a live join end to end: <c>GetPlayerObjectId()</c> read <c>0x7FFFFFFF</c> on every
/// single one of ~600 polls, then — separately — queried the SAME accessor on the HOST's own
/// long-established, fully-equipped character (a normal, hours-old save, nothing to do with
/// chargen) and got the IDENTICAL <c>0x7FFFFFFF</c>. <c>0x7FFFFFFF</c> is not a placeholder that
/// eventually resolves — it is the engine's own PERMANENT, reserved self-id for whichever
/// creature a client's <c>CSWSPlayer</c> currently drives, on every client, in every session,
/// forever. <see cref="Odyssey.ResolveServerObject"/> resolves it through the engine's own
/// <c>GetGameObject</c> exactly like any other id (round 4 confirmed: the resolve succeeds, and
/// the resolved creature's own <c>+0x04</c> field reads back the same <c>0x7FFFFFFF</c>, so the
/// object table is self-consistent about it) — there is no "real" numeric id to wait for, and
/// there never was.
///
/// So why did the original repair still never find the starting gear? Round 4's same capture
/// showed the actual gap: for the WHOLE original chargen-module window, both candidate
/// repositories <c>Odyssey.EquipUnequippedStartingGear</c> checks (the creature's own personal
/// backpack and the party-pool repository) read <c>count=0</c>, every poll — the items are not
/// LINKED into either repository at all while still inside the isolated single-player chargen
/// session, regardless of what id is used to address them. The bounded id-window scan around
/// <c>creatureObjectId</c> (<c>StartingGearIdWindow</c>) was ALSO never going to help for this id
/// specifically: <c>0x7FFFFFFF</c> is nowhere near the normal dynamic-pool id space every other
/// object (Trask, the items themselves) actually gets handed out of (measured this round: low
/// three-digit hex, e.g. <c>0x121</c>/<c>0x123</c>/<c>0x125</c>) — scanning ±32 around a reserved
/// sentinel can only ever find more of the same sentinel-adjacent nothing.
///
/// THE FIX: run the exact same repair again, unchanged, but only once — after the adopted-world
/// load that <see cref="MenuLoadController"/> services finishes (the SAME real, ordinary engine
/// module load every other join type already goes through: <c>CreateServer</c>, a genuine
/// character login, normal inventory linking), rather than inside the one-off, hand-rolled
/// in-process chargen replay (<see cref="CharGenDriveController"/>) that apparently never links a
/// fresh character's granted items into either repository at all. The repository-population gap
/// is the thing round 4 is betting a normal module load closes; it does not change what id is
/// used (<see cref="Odyssey.GetPlayerObjectId"/> still reports <c>0x7FFFFFFF</c> post-adopt, and
/// that is fine — round 4 also confirmed items genuinely possessed by the player get their own
/// <c>Possessor</c> field stamped with that SAME sentinel, so the existing exact-match check in
/// <see cref="Odyssey.EquipUnequippedStartingGear"/> is correct as written).
///
/// Armed by <see cref="NewCharacterCaptureController"/> the instant it captures a fresh chargen
/// character (before the quicksave even fires) so this controller is watching before the adopt's
/// own <c>MenuLoadController</c> request is even sent. Consumed the first time
/// <c>block.MenuLoadApplied</c> moves past the serial recorded at arm time WITH
/// <c>block.MenuLoadStatus == 0</c> — <see cref="MenuLoadController"/>'s own "the world is up and
/// on screen" ack, the earliest point a normal module load is safe to read from. One-shot: a
/// second arm (a second first-time joiner reroll in the same process) resets the whole state
/// machine cleanly, and a load that never succeeds (the adoption itself failed — see
/// <c>AdoptAndReconnectAsync</c>) simply leaves this disarmed with nothing to repair.
///
/// MAIN THREAD ONLY, runs from <c>PluginHost.MenuFrame</c> — the same hook
/// <see cref="MenuLoadController"/> and <see cref="NewCharacterCaptureController"/> already use,
/// immediately after <c>_menuLoad.OnFrame()</c> so this frame sees whatever that call just wrote.
/// </summary>
internal sealed class PostAdoptEquipRepairController(SharedBlockView block)
{
    /// <summary>How many polls the repository-population race gets after the world-up ack
    /// before this gives up. Generous relative to what round 4 actually measured (the repair
    /// ran successfully on the very first poll after the ack in every live capture) purely as
    /// insurance against a slower load on a colder disk — see this class's own doc comment for
    /// why no amount of extra time would have helped the OLD pre-save location, which is not the
    /// same claim as "extra time never helps here": here the wait is for ordinary inventory
    /// linking during a real module load, a race this codebase already treats cautiously
    /// elsewhere (see docs/ARCHITECTURE.md's transition-window rules).</summary>
    private const int MaxPolls = 20;

    private const int PollIntervalMs = 250;

    /// <summary>True from <see cref="Arm"/> until this controller has either repaired the
    /// character or given up.</summary>
    private bool _armed;

    /// <summary><c>block.MenuLoadApplied</c> as read at arm time — the load this controller is
    /// NOT interested in (whatever was already applied before the new-character flow's own
    /// request even exists). The first ack with a DIFFERENT serial is the adopted-world load.
    /// </summary>
    private uint _baselineMenuLoadApplied;

    /// <summary>-1 while still waiting for the adopted-world load's own ack; counts down once
    /// the ack has been seen and the repository-population poll begins.</summary>
    private int _pollsLeft = -1;

    private long _nextPollMs;

    /// <summary>
    /// Arms this controller to repair the NEXT adopted-world load's starting gear. Called by
    /// <see cref="NewCharacterCaptureController"/> the moment it captures a fresh character —
    /// before the quicksave even completes, so this is watching well before
    /// <c>JoinFlow.CreateCharacterThenAdoptAsync</c> requests the load
    /// (<c>SessionManager.AdoptWithNewCharacterAsync</c> -&gt;
    /// <c>bridge.TryRequestAdoptedWorldLoad</c>) that bumps <c>block.MenuLoadSerial</c>/
    /// <c>MenuLoadApplied</c>. MAIN THREAD ONLY (reads <c>block.MenuLoadApplied</c> directly).
    /// </summary>
    public void Arm()
    {
        _armed = true;
        _baselineMenuLoadApplied = block.MenuLoadApplied;
        _pollsLeft = -1;
        _nextPollMs = 0;
    }

    public void OnFrame()
    {
        if (!_armed)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        bool waitingForLoad = _pollsLeft < 0;

        PostAdoptEquipRepairAction action = PostAdoptEquipRepairRule.Decide(
            waitingForLoad, block.MenuLoadApplied, _baselineMenuLoadApplied, block.MenuLoadStatus,
            nowMs, _nextPollMs, _pollsLeft);

        switch (action)
        {
            case PostAdoptEquipRepairAction.Wait:
            case PostAdoptEquipRepairAction.WaitForNextPoll:
                return;

            case PostAdoptEquipRepairAction.GiveUpLoadFailed:
                // The adoption's own load failed — AdoptAndReconnectAsync has already told the
                // player why (JoinFlow.cs). Nothing here to repair against.
                PluginHost.Log(
                    "post-adopt equip repair: the adopted-world load failed "
                    + $"(status {block.MenuLoadStatus}) - nothing to repair");
                _armed = false;
                return;

            case PostAdoptEquipRepairAction.StartPolling:
                _pollsLeft = MaxPolls;
                _nextPollMs = 0;
                // Fall straight into the first poll this same frame rather than waiting another
                // MenuFrame tick — the ack just landed, nothing is gained by delaying.
                break;

            case PostAdoptEquipRepairAction.GiveUpPollBudgetExhausted:
                PluginHost.Log(
                    $"post-adopt equip repair: gave up after {MaxPolls} poll(s) - the joiner's "
                    + "starting gear, if any was still unequipped, was not found; see the "
                    + "newchar-diag lines above for the candidate search this same poll ran");
                _armed = false;
                return;
        }

        _nextPollMs = nowMs + PollIntervalMs;

        uint playerObjectId = Odyssey.GetPlayerObjectId();
        int repaired = playerObjectId == 0 ? 0 : Odyssey.EquipUnequippedStartingGear(playerObjectId);

        if (repaired > 0)
        {
            PluginHost.Log(
                $"post-adopt equip repair: equipped {repaired} starting item(s) the joiner's "
                + $"chargen left unequipped (player id 0x{playerObjectId:X}, "
                + $"{MaxPolls - _pollsLeft + 1} poll(s) after the adopted world came up)");
            _armed = false;
            return;
        }

        _pollsLeft--;
    }
}
