namespace KotorCoop.Plugin;

/// <summary>
/// Applies "this floor mine was disarmed or detonated on a peer" (a kind-1 entry in the
/// placeable slot list, SharedBlock.Mines.cs) by removing the same mine here. coop.ini
/// <c>mine_sync</c>, default ON since the two-way live proof in docs/research/mines.md §9.
///
/// The removal is the engine's OWN path, not a field poke: <see cref="Odyssey.DestroyObjectById"/>
/// runs the NWScript DestroyObject command, whose tail
/// (<c>CSWVirtualMachineCommands::ExecuteCommandDestroyObject 0x0052FF20</c>, 0x0053012F-0x0053015C)
/// queues event <c>[0x007441E0]</c> (= 11) at the object through
/// <c>CServerAIMaster::AddEventDeltaTime</c>. That is byte-for-byte the event
/// <c>CSWSTrigger::OnEnterMine</c> posts at itself when a one-shot mine fires (0x0058D6D3-0x0058D6EC)
/// and <c>CSWSCreature::AIActionDisarmMine</c> posts on a successful disarm (0x005199C9-0x005199EF).
/// <c>CSWSTrigger::EventHandler</c> answers it (0x0058F197-0x0058F1BA) by running
/// <c>CSWSTrigger::RemoveFromArea 0x0058EE30</c> and the virtual deleting destructor, so the
/// derived state (area trigger list, area object list) is rebuilt by the engine, never by us.
///
/// Nothing here writes a field. Refusals (<see cref="MineRemovalRule"/>) keep an id that drifted
/// onto an ordinary trigger from ever deleting it.
/// </summary>
internal sealed class MineController
{
    private static readonly bool Enabled = PluginConfig.ReadBool("mine_sync", true);

    private readonly HashSet<uint> _requested = [];
    private int _logged;

    /// <summary>Forgets every posted destroy on a module change; ids now name other objects.</summary>
    public void Invalidate() => _requested.Clear();

    /// <summary>Applies one removal entry. MAIN THREAD ONLY (DestroyObjectById drives the VM).</summary>
    public void Apply(uint objectId)
    {
        if (!Enabled)
        {
            return;
        }

        // The publisher only emits deterministic GIT ids (0..0xFFFF). Keep the same bound on
        // the receive side before ResolveServerObject/ResolveTrigger touches an engine pointer;
        // dynamic ids belong to party-placed mines and are never valid shared mine targets.
        if (objectId == 0 || objectId == Odyssey.ObjectInvalid || objectId > 0xFFFF)
        {
            return;
        }

        nint trigger = OdysseyTriggers.ResolveTrigger(objectId);
        bool isTrap = false;
        bool destroyable = false;
        bool partyPlaced = false;
        if (trigger != 0)
        {
            unsafe
            {
                isTrap = *(int*)(trigger + TriggerTrapGate) != 0;
                destroyable = *(int*)(trigger + ObjectIsDestroyable) != 0;
                partyPlaced = *(int*)(trigger + TriggerPlacedByParty) != 0;
            }
        }

        MineRemovalDecision decision = MineRemovalRule.Decide(
            Enabled, _requested.Contains(objectId), trigger != 0, isTrap, destroyable, partyPlaced);

        switch (decision)
        {
            case MineRemovalDecision.Remove:
                if (Odyssey.DestroyObjectById(objectId, out int failure))
                {
                    _requested.Add(objectId);
                    LogFew($"mine 0x{objectId:X}: removed on the session's authority (engine DestroyObject)");
                }
                else
                {
                    // Failure 4 = the script VM was mid-call; the next pass retries.
                    LogFew($"mine 0x{objectId:X}: DestroyObject refused (failure {failure}); will retry");
                }

                break;

            case MineRemovalDecision.NotAMine:
                LogFew($"mine 0x{objectId:X}: refused - a trigger with no trap gate, not deleting it");
                break;

            case MineRemovalDecision.PartyPlaced:
                LogFew($"mine 0x{objectId:X}: refused - party-placed mine has a local dynamic id");
                break;

            case MineRemovalDecision.NotDestroyable:
                LogFew($"mine 0x{objectId:X}: refused - is_destroyable is 0");
                break;
        }
    }

    /// <summary>CSWSTrigger + this = trap/mine gate; nonzero = mine. VERIFIED (docs/research/mines.md §2).</summary>
    private const int TriggerTrapGate = 0x2BC;

    /// <summary>CSWSTrigger + this = "placed by the player's party". Nonzero means the id is
    /// dynamic and local to this instance, so it cannot be targeted by a shared GIT-mine flag.
    /// VERIFIED in docs/research/mines.md §2.</summary>
    private const int TriggerPlacedByParty = 0x314;

    /// <summary>CSWSObject + this = is_destroyable (symbol database; read by
    /// CSWSTrigger::EventHandler's event-11 branch at 0x0058F19F).</summary>
    private const int ObjectIsDestroyable = 0xEC;

    private void LogFew(string message)
    {
        if (_logged < 20)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
