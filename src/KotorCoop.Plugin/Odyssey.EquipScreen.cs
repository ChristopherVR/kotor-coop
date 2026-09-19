namespace KotorCoop.Plugin;

// The retail Equipment screen (CSWGuiInGameEquip) — who it is showing. Read by
// CompanionEquipSuppressionController on a client. See docs/research/companion-state-sync.md
// §14 (2026-09-18) for the disassembly this rests on.
internal static unsafe partial class Odyssey
{
    /// <summary>
    /// CGuiInGame + this = the in-game screen array (CSWGuiPanel*[]) — index 0 is the Equipment
    /// screen, <c>CSWGuiInGameEquip*</c>. VERIFIED by disassembly 2026-09-18:
    /// <c>CGuiInGame::GetCurrentEquipScreenNPC 0x0062FEA0</c> reads <c>[this+0x0C]</c>, checks it
    /// with <c>CSWGuiManager::PanelExists</c> and returns <c>(sbyte)[panel+0x4274]</c>;
    /// <c>HideSWInGameGui 0x0062CBA0</c> removes <c>[this + [this+0x2C]*4 + 0x0C]</c> — the array
    /// indexed by the current screen number.
    /// </summary>
    private const int InGameGuiEquipScreenOffset = 0x0C;

    /// <summary>CGuiInGame + this = which in-game screen is up (0 = Equipment). Same
    /// disassembly as above (<c>HideSWInGameGui</c>'s <c>[esi+0x2C]</c> index).</summary>
    private const int InGameGuiCurrentScreenOffset = 0x2C;

    /// <summary>CSWGuiInGameEquip's vtable — tools/kotor1_0_3.db <c>classes.vtable</c> for the
    /// class (7694752). The guard that <c>[gui+0x0C]</c> really is this panel before anything
    /// below it is read.</summary>
    private const nint EquipScreenVtable = 0x007567A0;

    /// <summary>CSWGuiInGameEquip + this = <c>player_creature</c> (CSWCCreature*), the character
    /// the screen is dressing — symbol DB <c>offsets</c> row, and VERIFIED by disassembly of
    /// <c>CSWGuiInGameEquip::SetCharacter 0x006B7590</c> (<c>cmp eax,[edi+0x64]</c> then
    /// <c>mov [edi+0x64],eax</c> — the character arrow buttons store the new subject here).</summary>
    private const int EquipScreenSubjectOffset = 0x64;

    internal enum EquipScreenSubject
    {
        /// <summary>No module, no HUD, the screen is not up, or it is showing nobody yet.</summary>
        NotOpen,

        /// <summary>The Equipment screen is up, showing this instance's own controlled PC.</summary>
        Player,

        /// <summary>The Equipment screen is up, showing someone else — a companion.</summary>
        Companion,

        /// <summary>A pointer did not look like what the offsets above assume — stop reading.</summary>
        Unreadable,
    }

    /// <summary>
    /// Who the retail Equipment screen is showing, if it is up. Every read is guarded the way
    /// <see cref="PartySelectSuppressionController"/>'s own walk is (range-checked pointers, the
    /// vtable compared before the panel is trusted), and the panel is only believed to be on
    /// screen when the GUI manager itself says it is registered — never from the raw pointer,
    /// which is a persistent <c>CGuiInGame</c> member that outlives every open/close.
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static EquipScreenSubject ReadEquipScreenSubject(out nint panel, out nint subject, out string note)
    {
        panel = 0;
        subject = 0;
        note = string.Empty;

        nint client = GetClient();
        nint gui = GetInGameGui();
        if (client == 0 || gui == 0 || *(int*)(gui + InGameGuiInitializedOffset) == 0)
        {
            return EquipScreenSubject.NotOpen;
        }

        // The menu-key screens are the only ones that set CGuiInGame+0x30 through
        // Show/HideSWInGameGui (Odyssey.Store.cs, InGameGuiBusyOffset); with it clear, or with a
        // different screen current, the equip panel is not what is on screen.
        if (*(int*)(gui + InGameGuiBusyOffset) == 0 || *(int*)(gui + InGameGuiCurrentScreenOffset) != 0)
        {
            return EquipScreenSubject.NotOpen;
        }

        nint manager = Deref(gui + InGameGuiManagerOffset);
        nint candidate = Deref(gui + InGameGuiEquipScreenOffset);
        if (candidate == 0)
        {
            return EquipScreenSubject.NotOpen;
        }

        if (!LooksLikeHeapPointer(manager) || !LooksLikeHeapPointer(candidate))
        {
            note = $"gui+0x38 0x{manager:X} / gui+0x0C 0x{candidate:X} did not look like heap pointers";
            return EquipScreenSubject.Unreadable;
        }

        if (GuiPanelExistsFn(manager, candidate) == 0)
        {
            return EquipScreenSubject.NotOpen;
        }

        nint vtable = Deref(candidate);
        if (vtable != EquipScreenVtable)
        {
            note = $"panel 0x{candidate:X} vtable 0x{vtable:X} is not CSWGuiInGameEquip (0x{EquipScreenVtable:X})";
            return EquipScreenSubject.Unreadable;
        }

        panel = candidate;
        subject = Deref(candidate + EquipScreenSubjectOffset);
        if (subject == 0)
        {
            return EquipScreenSubject.NotOpen;
        }

        if (!LooksLikeHeapPointer(subject))
        {
            note = $"panel+0x64 0x{subject:X} did not look like a creature pointer";
            return EquipScreenSubject.Unreadable;
        }

        nint controlled = GetClientPlayerCreatureFn(client);
        return subject == controlled ? EquipScreenSubject.Player : EquipScreenSubject.Companion;
    }
}
