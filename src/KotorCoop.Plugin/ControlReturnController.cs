namespace KotorCoop.Plugin;

/// <summary>
/// Connecting must never leave a client watching, or driving, a body the session has taken
/// over — and it did.
///
/// ⚠ THE HOLE THIS FILLS. On a client every companion is a host-driven mirror, so the agent
/// pins input to the player's own PC by writing the controlled-character id
/// (<c>CClientExoAppInternal + 0x20</c>, docs/research/client-control-lock.md) back to the PC
/// every tick. That reverts CONTROL. It does not move the CAMERA: the engine's own camera
/// handoff lives inside <c>ChangeCharacter</c> (the <c>SetCameraFollow</c> at 0x5F6C27), and
/// an external id write does not go through it. So a player who was playing AS Trask at the
/// moment they connected got their PC's controls back with the camera still bolted to Trask —
/// and could not fix it, because the same session arms the byte patch that makes
/// <c>ChangeCharacter</c> return immediately. Tab dead, portraits dead, view stuck. The same
/// thing happens on an auto-reconnect, which is unattended by design.
///
/// So this closes the loop, as a LEVEL rather than a one-shot: whenever a mirroring instance
/// finds the player driving a party-roster companion it says so, and when control lands back
/// on the player's own character it points the camera there through the engine's own
/// <c>CSWCModule::SetCameraFollow</c> — re-asserting a bounded number of passes, because that
/// call silently does nothing while the client is in combat mode.
///
/// It deliberately does NOT write the control id itself. That lever is the agent's, it runs
/// at ~30 Hz, and a second writer of the same field in a second process is how you get two
/// halves disagreeing about who the player is. This half owns the camera only.
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

        nint controlled = Odyssey.GetPlayerCreaturePointer();
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
                if (_claimLogged != controlledId)
                {
                    _claimLogged = controlledId;

                    // Names the id it saw, because the interesting failure is this line
                    // repeating for the same id: that means the agent's control lock is not
                    // running (party sync off, or the Party capability dropped) and the player
                    // is still driving a body the host owns.
                    PluginHost.Log(
                        $"control: you are driving companion 0x{controlledId:X}, which this "
                        + "session owns — handing you back your own character");
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
