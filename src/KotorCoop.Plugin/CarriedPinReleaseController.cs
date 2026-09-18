using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Releases a min-1-hit-point pin that rode into a freshly loaded world ON the carried
/// character, so a joiner can actually be downed.
///
/// THE BUG THIS CLOSES (docs/review-seamless-2026-09-18.md blocker #8). The Endar Spire's
/// command deck pins the PC at 1 hp from its area OnEnter (`k_pend_area01`:
/// `SetMinOneHP(GetFirstPC(), TRUE)`, 0.1 s after entry) and only the starboard section clears
/// it (`k_pend_1b_area`). The flag is a persistent creature field: it rides through saves, and
/// docs/research/min-one-hitpoint.md measured it riding a warp into `tar_m10aa`. A
/// `join_mode = auto` character is captured by quicksave moments after end_m01aa loads
/// (<see cref="NewCharacterCaptureController"/>), so every co-op character made that way was
/// pinned for life — the rig's own joiner sidecar read `Min1HP = 1` — and a `save`-mode
/// character from the same deck is pinned the same way. Consequence: the carried character
/// never drops below 1 hp anywhere, so the downed/revive/wipe machinery never engages for it.
///
/// Two halves close it. <c>PlayerIfo.BuildPifo</c> now strips the field from every character
/// file it writes (sidecar and carried pifo), which fixes every character captured from now on.
/// This controller covers the ones already captured: after every plugin-serviced world load
/// (<see cref="MenuLoadController"/> — the path every adoption and rejoin goes through, and the
/// rig's <c>loadgame</c> verb), it polls until the engine's player lookup resolves and the
/// module name is readable, then — unless the module is one that pins at entry
/// (<see cref="CarriedPinReleaseRule.ModulesThatPinAtEntry"/>, where the flag is the module's
/// own and a race against its OnEnter must be avoided) — clears the flag through the SAME
/// write the <c>god</c> cheat and the enemy pin already use (<see cref="Odyssey.SetMinOneHitPoint"/>,
/// live-verified). Modules that pin LATER (the Taris duel ring, around each duel) are
/// unaffected: this runs once per load, at world-up, before any such script has fired.
///
/// One-shot per load: the watch re-arms on every change of <c>block.MenuLoadApplied</c> and
/// disarms after a bounded poll budget, a skip, a no-op or a release. A HOST never goes
/// through <see cref="MenuLoadController"/> in normal play (its own Load Game is the retail
/// menu), so a host's <c>god</c> pin survives its doors; a developer who typed <c>god on</c> and
/// then ran <c>loadgame</c> loses it and the log says so.
///
/// MAIN THREAD ONLY, runs from <c>PluginHost.MenuFrame</c> right after
/// <see cref="PostAdoptEquipRepairController"/>, for the same reason that one lives there: the
/// swap hook is dormant across a menu-load screen, and the "world is up" ack lands here.
/// </summary>
internal sealed unsafe class CarriedPinReleaseController(SharedBlockView block)
{
    /// <summary>Polls after the ack before giving up. The player creature resolves on the very
    /// first poll after a normal load; the budget is insurance against a cold disk.</summary>
    private const int MaxPolls = 40;

    private const int PollIntervalMs = 250;

    private bool _baselined;
    private uint _lastMenuLoadApplied;
    private int _pollsLeft;
    private long _nextPollMs;

    public void OnFrame()
    {
        uint applied = block.MenuLoadApplied;

        if (!_baselined)
        {
            // Whatever was applied before this plugin instance started watching is not a load
            // this controller saw come up; only a CHANGE from here on is.
            _baselined = true;
            _lastMenuLoadApplied = applied;
            return;
        }

        if (applied != _lastMenuLoadApplied)
        {
            _lastMenuLoadApplied = applied;
            _pollsLeft = block.MenuLoadStatus == 0 ? MaxPolls : 0;
            _nextPollMs = 0;
        }

        if (_pollsLeft <= 0)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs < _nextPollMs)
        {
            return;
        }

        _nextPollMs = nowMs + PollIntervalMs;

        nint player = Odyssey.GetPlayerCreaturePointer();

        byte* moduleBuf = stackalloc byte[SharedBlock.ModuleNameLength];
        int moduleLength = Odyssey.ReadCurrentModuleName(moduleBuf, SharedBlock.ModuleNameLength);
        string module = moduleLength > 0 ? Encoding.ASCII.GetString(moduleBuf, moduleLength) : string.Empty;

        bool pinned = player != 0 && Odyssey.GetMinOneHitPoint(player);

        switch (CarriedPinReleaseRule.Decide(player != 0, module, pinned))
        {
            case CarriedPinReleaseAction.NotReady:
                _pollsLeft--;
                if (_pollsLeft == 0)
                {
                    PluginHost.Log(
                        $"carried pin: gave up after {MaxPolls} poll(s) - the player never resolved "
                        + $"(module '{module}'); a carried min-1-hp pin, if any, is still set");
                }

                return;

            case CarriedPinReleaseAction.LeaveModulePin:
                PluginHost.Log($"carried pin: {module} pins the player from its own OnEnter - leaving the flag to the module");
                _pollsLeft = 0;
                return;

            case CarriedPinReleaseAction.NothingToRelease:
                PluginHost.Log($"carried pin: none on the player at world-up in {module}");
                _pollsLeft = 0;
                return;

            case CarriedPinReleaseAction.Release:
                Odyssey.SetMinOneHitPoint(player, false);
                PluginHost.Log(
                    $"carried pin: released the min-1-hp pin the player carried into {module} "
                    + $"(0x{(uint)player:X}; hp {Odyssey.GetCurrentHitPoints(player)}) - the character can be downed again. "
                    + "A pin the Endar Spire's tutorial set rode in through the character file; new "
                    + "character files no longer carry it. If this was a deliberate 'god on', type it again");
                _pollsLeft = 0;
                return;
        }
    }
}
