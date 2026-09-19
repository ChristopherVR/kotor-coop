namespace KotorCoop.Plugin;

/// <summary>
/// Keeps a CLIENT off its companions' equipment pages (coop.ini <c>companion_equip_host_only</c>,
/// default ON, NEEDS-LIVE) — the companion sibling of <see cref="PartySelectSuppressionController"/>,
/// for the reason <see cref="CompanionEquipSuppressionRule"/>'s own doc comment gives: companion
/// gear and the party pool are both host-owned, so a client's equip on a companion loses the
/// item. See docs/research/companion-state-sync.md §14 (2026-09-18).
///
/// Unlike the party-select screen this does NOT mark the panel for the manager's delete sweep:
/// the Equipment screen is a persistent <c>CGuiInGame</c> member (index 0 of its screen array,
/// <see cref="Odyssey.ReadEquipScreenSubject"/>), and deleting it would leave a dangling pointer
/// the next Equip key would dereference. It closes the screen the way the retail Back button
/// does — <see cref="Odyssey.CloseFullScreenGui"/>, i.e. <c>HideSWInGameGui</c> followed by the
/// input-class restore a retail close performs — the same primitive the <c>closegui</c> live
/// verb has driven on the rig since 2026-08-28.
///
/// Reads every 10 frames (a pointer walk and one engine call, cheap), and closes AT MOST ONCE
/// per visit: after a close the rule stays disarmed until the screen is seen closed or showing
/// the PC again, so an engine refusal (the GUI manager's modal stack is not empty — the one
/// case <c>HideSWInGameGui</c> bails on) is logged once and never retried in a loop.
/// </summary>
internal sealed class CompanionEquipSuppressionController(SharedBlockView block)
{
    private const int IntervalFrames = 10;

    private readonly bool _enabled = PluginConfig.ReadBool("companion_equip_host_only", true);

    /// <summary>Latched once a guard refuses a pointer: a wrong offset assumption is wrong for
    /// the whole process, not per module — the same idiom the party-select controller uses.</summary>
    private bool _guardFailed;

    private bool _armed = true;
    private int _countdown;
    private int _closesLogged;

    public void OnFrame()
    {
        if (!_enabled || _guardFailed)
        {
            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        bool isClient = block.TryGetHostPartyRoster(out _, out _);
        if (!isClient)
        {
            // Not a client with a live host roster: never even walk the GUI. Re-arm so the
            // first companion page after a (re)join is handled.
            _armed = true;
            return;
        }

        Odyssey.EquipScreenSubject subject = Odyssey.ReadEquipScreenSubject(out nint panel, out nint who, out string note);

        switch (CompanionEquipSuppressionRule.Decide(_enabled, isClient, subject, ref _armed))
        {
            case CompanionEquipSuppressionRule.Action.Disable:
                _guardFailed = true;
                PluginHost.Log($"companion equip: guard refused ({note}) — suppression disabled for this session");
                return;

            case CompanionEquipSuppressionRule.Action.Close:
                bool closed = Odyssey.CloseFullScreenGui(out string detail);
                block.PostLocalChatLine("the host equips companions");
                if (_closesLogged < 20)
                {
                    _closesLogged++;
                    PluginHost.Log(
                        $"companion equip: client copy of the Equipment screen (panel 0x{panel:X}) was showing "
                        + $"creature 0x{who:X}, not the PC — {(closed ? "closed" : "close REFUSED")}: {detail}");
                }

                return;
        }
    }

    /// <summary>A module transition or session end: nothing here caches an engine pointer, so
    /// only the one-close latch is reset — a fresh world's first companion page deserves the
    /// same one-time close a first-ever one would.</summary>
    public void Invalidate()
    {
        _armed = true;
        _countdown = 0;
    }
}
