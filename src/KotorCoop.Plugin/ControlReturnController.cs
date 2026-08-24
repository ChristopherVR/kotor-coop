namespace KotorCoop.Plugin;

/// <summary>
/// Connecting must never leave a client watching, or driving, a body the session has taken
/// over — and it did.
///
/// ⚠ THE HOLE THIS FILLS. On a client every companion is a host-driven mirror, so a player who
/// was playing AS Trask at the moment they connected must get their own PC's controls AND
/// camera back — and could not, because the same session arms the byte patch that makes
/// <c>ChangeCharacter</c> (the GUI/keyboard switch funnel) return immediately. Tab dead,
/// portraits dead, view stuck. The same thing happens on an auto-reconnect, which is
/// unattended by design.
///
/// So this closes the loop, as a LEVEL rather than a one-shot: whenever a mirroring instance
/// finds the player driving a party-roster companion it says so, hands control back
/// (<see cref="Odyssey.SwitchControlToPc"/>) by reordering CSWParty's own member array so the
/// PC becomes member[0] again — the SAME end state a real Tab press or portrait click produces
/// (client-control-lock.md) — and re-asserts the camera through the engine's own,
/// already-working <c>CSWCModule::SetCameraFollow</c> for a bounded number of passes afterward,
/// because that call silently does nothing while the client is in combat mode.
///
/// ⚠ ROUND 70 (2026-08-22): earlier rounds detected "is the player driving a companion" through
/// <see cref="Odyssey.GetPlayerCreaturePointer"/> and never wrote the control id at all — both
/// wrong. Disassembly this round found <c>GetPlayerCreaturePointer</c>
/// (<c>CServerExoApp::GetPlayerCreature</c>, 0x4B2820) resolves a FIXED "player slot 0" entry
/// in the server's own connected-players list, a value no control switch ever touches, so the
/// claim never fired; and the AGENT's own per-tick revert (<c>LockControlToPlayer</c>, a
/// different process, a different field) never reordered CSWParty's member array, so even when
/// input authority nominally came back the array stayed in the "companion first" shape
/// <c>PartyTogetherClampRule</c> exists to defer around. Both are fixed here: detection reads
/// <see cref="Odyssey.GetControlledCreaturePointer"/> (the field a real switch actually moves),
/// and the hand-back performs the SAME array reorder the engine's own
/// <c>ChangeCharacter</c>/<c>SetControlledCreature</c> funnel was supposed to (0x5F6B60/0x635480
/// — fully disassembled and its calling convention confirmed this round, but calling it live
/// left the array completely unchanged in every test; see <c>Odyssey.RotateControlToFront</c>'s
/// own remarks for the full NEEDS-LIVE writeup) plus the engine's own, separately-verified
/// camera call — not one authoritative engine call, but the same net effect from two working
/// pieces instead of one that, live, was not.
///
/// Fail-safe direction is fixed: the player controls their own PC. If the hand-back cannot be
/// made — no client module, combat mode for the whole budget — the worst case is the stock
/// camera the engine puts back on the next area load, never a client driving a shared body.
///
/// Kill switch: <c>control_return=0</c> in coop.ini.
/// </summary>
internal sealed unsafe class ControlReturnController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("control_return", true);

    /// <summary>Frames between passes. Control changes on a human timescale and the agent's
    /// revert lands within a tick, so this is cadence, not a race.</summary>
    private const int IntervalFrames = 10;

    /// <summary>How many passes a hand-back re-asserts for — about five seconds. Long enough
    /// to outlast a combat-mode window (which is what swallows the call), short enough that
    /// this never becomes the thing that owns the camera.</summary>
    private const int AssertBudget = 30;

    /// <summary>The engine's own bound on NPC roster slots: <c>CSWPartyTable::AddMember</c>
    /// refuses anything &gt;= 9 (0x00565648). See docs/research/party-reconciliation.md.</summary>
    private const int PartyNpcSlots = 9;

    private int _countdown;
    private int _assertsLeft;

    /// <summary>The companion id already reported, so a player parked on one does not fill the
    /// log — but a DIFFERENT one still gets its own line.</summary>
    private uint _claimLogged;

    private bool _handBackLogged;

    /// <summary>Live-found 2026-08-24 (docs/research/party-reconciliation.md): the same
    /// warp-transition epoch race <see cref="PartyFollowTransitionRule"/> closed for
    /// <c>PartyFollowController</c> (round 85/87 — an AV inside the engine's own CSWParty
    /// machinery, one frame after a fresh module epoch reads valid but the new module's party
    /// data is not necessarily populated yet). This controller's own per-frame engine calls
    /// (<see cref="Odyssey.GetControlledCreaturePointer"/>/<see cref="Odyssey.SwitchControlToPc"/>,
    /// both of which walk CSWParty's raw member array exactly like the calls that rule already
    /// guards) were just as unconditional and just as reachable in that same first frame, and
    /// had no epoch check at all. Reproduced live, twice in a row, on a rig cross-module warp:
    /// the client's own plugin log shows the "control: you are driving companion 0x... —
    /// switch not ready yet, retrying" line as the LAST line before the process silently exits
    /// (no crash dump, no WER event — a clean exit, not caught as an access violation this
    /// time, but the same shape: this controller's engine calls firing within ~150ms-1s of
    /// "epoch: world unloading" for the destination module). See
    /// <see cref="PartyFollowTransitionRule"/>'s own remarks for the full disassembly-grounded
    /// finding this reuses verbatim.</summary>
    private ulong _lastSeenEpoch;

    /// <summary>The session ended: drop the budget. Control is the player's again, and the
    /// camera with it — including, deliberately, if they want it back on a companion.</summary>
    public void ReleaseSession()
    {
        _assertsLeft = 0;
        _claimLogged = 0;
        _handBackLogged = false;
        _countdown = 0;
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        // Must run before ANY of this controller's engine calls below, not just the ones the
        // countdown currently happens to gate — see this rule's own remarks (shared verbatim
        // with PartyFollowController) for why the dangerous frame is the first one after a
        // module warp, not a steady-state cadence question.
        bool worldEpochReadable = Odyssey.TryGetWorldEpoch(out ulong epoch);
        if (PartyFollowTransitionRule.ShouldSkipThisFrame(worldEpochReadable, epoch, _lastSeenEpoch))
        {
            if (worldEpochReadable)
            {
                _lastSeenEpoch = epoch;
            }

            // Whatever this controller's countdown/claim bookkeeping held belongs to the world
            // that just went away — a stale claimed-id or a mid-countdown from before the warp
            // must not suppress or skew the first real reading the settled frame after this one
            // takes.
            _countdown = 0;
            _claimLogged = 0;
            _handBackLogged = false;
            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        // "This instance mirrors the world" — a client. Same predicate PortraitController
        // hides the party widgets on, and for the same reason: these are the instances whose
        // companions belong to somebody else.
        bool mirroring = block.Command == SharedBlock.CommandSpawnAndDrive && block.DamageDealtEnabled;

        nint controlled = Odyssey.GetControlledCreaturePointer();
        uint controlledId = controlled != 0 ? *(uint*)(controlled + 0x04) : 0;

        // Dialogue owns the camera completely, and so does the spectate camera while the
        // player is down — both are documented owners with their own re-assert loops.
        bool cameraOwnedElsewhere =
            Odyssey.IsConversationActive || (controlled != 0 && Odyssey.IsDowned(controlled));

        bool sessionOwned = mirroring && controlledId != 0 && IsPartyRosterBody(controlledId);

        (ControlReturnAction action, _assertsLeft) = ControlReturnRule.Decide(
            mirroring, cameraOwnedElsewhere, sessionOwned, _assertsLeft, AssertBudget);

        switch (action)
        {
            case ControlReturnAction.NoteClaim:
                _handBackLogged = false;

                // The actual hand-back — see the class remarks (Round 70) for why a raw field
                // poke was never enough: it never reordered CSWParty's member array, so
                // PartyTogetherClampRule kept deferring forever with nothing left to converge
                // toward. Safe to call every pass while this is true: on an already-correct
                // array, Odyssey.SwitchControlToPc's own index lookup finds the PC already at
                // member[0] and the rotate is a genuine no-op.
                bool switched = Odyssey.SwitchControlToPc();

                if (_claimLogged != controlledId)
                {
                    _claimLogged = controlledId;

                    // Names the id it saw, because the interesting failure is this line
                    // repeating for the same id: that would mean the engine switch is
                    // refusing every attempt (no client world, or the PC is not currently a
                    // CSWParty member — should not happen) and the player is still driving a
                    // body the host owns.
                    PluginHost.Log(
                        $"control: you are driving companion 0x{controlledId:X}, which this "
                        + "session owns — handing you back your own character"
                        + (switched ? " - array reordered and camera pointed at your PC" : " (switch not ready yet, retrying)"));
                }

                break;

            case ControlReturnAction.AssertCamera:
                // "Asked" rather than "moved": SetCameraFollow returns nothing and silently
                // does nothing while the client is in combat mode, which is the whole reason
                // this is a budget of passes rather than one call. False means there was no
                // client world to ask at all, which IS worth distinguishing.
                bool asked = Odyssey.RestoreCameraToControlledCreature();
                if (!_handBackLogged)
                {
                    _handBackLogged = true;
                    _claimLogged = 0;
                    PluginHost.Log(asked
                        ? $"control: putting the camera back on your own character (0x{controlledId:X})"
                        : "control: no client world to give the camera back to yet");
                }

                break;

            case ControlReturnAction.Nothing:
            default:
                break;
        }
    }

    /// <summary>
    /// Whether an object id is one of this instance's party companions.
    ///
    /// The whole roster (0..8) rather than only the slots the session is currently publishing:
    /// at the moment of connect the companion presence has not arrived yet, and that is
    /// precisely the moment this has to be right. On a client no roster body is ever
    /// legitimately controlled anyway — the control lock forbids all of them.
    /// </summary>
    private static bool IsPartyRosterBody(uint objectId)
    {
        for (int slot = 0; slot < PartyNpcSlots; slot++)
        {
            uint id = Odyssey.GetPartyRosterObjectId(slot);
            if (id == objectId && id is not 0 and not Odyssey.ObjectInvalid)
            {
                return true;
            }
        }

        return false;
    }
}
