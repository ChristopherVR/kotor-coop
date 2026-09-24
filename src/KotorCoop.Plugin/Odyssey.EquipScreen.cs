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
    // 0x007569A0: written by CSWGuiInGameEquip's own constructor (0x006BA9CD) and destructor
    // (0x006B8864). This read 0x007567A0 until 2026-09-25, a CSWGuiContainer vtable (its constructor
    // writes it at 0x006B6FF5), so the companion-equip guard disabled itself on its first real read.
    private const nint EquipScreenVtable = 0x007569A0;

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

    // ---- showgui equip: open the retail Equipment screen for a chosen party member ----------
    //
    // `showgui equip [partyIndex]` (2026-09-25, docs/research/companion-state-sync.md's dated
    // follow-up to §14): the rig cannot press the retail Equip key or click a portrait arrow, so
    // `CompanionEquipSuppressionController`'s own close ("the host equips companions") has never
    // been seen firing on a live client. This opens the SAME screen through the SAME two engine
    // entry points the key and the arrow buttons themselves reach.
    //
    // `CGuiInGame::ShowSWInGameGui(this, int) 0x0062C9B0` — VERIFIED by fresh disassembly:
    // `ret 4` (one stack argument), and `xrefs.py 0x62c9b0` finds exactly two callers, both
    // inside the retail key-handler dispatcher at 0x00621308 (`push 7; call` at 0x00621480,
    // `push eax; call` — `eax` computed per-key — at 0x00621981). Tracing what the function's
    // BODY actually reads to pick a screen (0x0062CA2E-0x0062CA57) settles it is NOT the stack
    // argument: it calls into the VM's own global-variable accessor (`ecx=[0x7A3A00]`, the same
    // global VM pointer `CSWVirtualMachineCommands::ExecuteCommandGetGlobalNumber` 0x00538DC0
    // uses at the identical `mov ecx,[0x7a3a00]`) for a NUMBER global named literally
    // `"k_sup_guiopen"` (read straight out of the exe's data section at the referenced literal
    // 0x7513BC — confirmed by dumping those bytes), clamps it to [0,8), and stores it at
    // <see cref="InGameGuiCurrentScreenOffset"/> — falling back to whatever screen was already
    // showing if the global is absent/out of range. So the screen this call opens is whatever
    // `k_sup_guiopen` currently says, which is why <see cref="ShowEquipScreenForPartyMember"/>
    // sets it to 0 (Equipment) through the ALREADY-VERIFIED <see cref="TryWriteGlobal"/> (the
    // same typed setter `setglobal` drives) immediately before calling this, then VERIFIES the
    // result rather than trusting it — refusing instead of guessing when the write does not
    // stick (e.g. the name is undeclared in this build).
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> ShowSWInGameGuiFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x0062C9B0;

    /// <summary>
    /// <c>CSWGuiInGameEquip::SetCharacter(this, CSWCCreature* creature)</c> 0x006B7590 —
    /// VERIFIED, fresh disassembly (`ret 4`; the party arrow buttons' own "set subject" call,
    /// already cited by <see cref="EquipScreenSubjectOffset"/>'s doc comment). Early-outs when
    /// <paramref name="creature"/> already matches <c>panel+0x64</c>; otherwise writes it there
    /// and refreshes the on-screen slot icons — exactly what a real arrow click does, just aimed
    /// directly at the wanted party member instead of stepping one at a time.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> SetCharacterFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x006B7590;

    /// <summary>The <c>k_sup_guiopen</c> global name, ASCII, no null terminator counted in the
    /// length passed to <see cref="TryWriteGlobal"/> (that helper appends its own).</summary>
    private static ReadOnlySpan<byte> GuiOpenGlobalName => "k_sup_guiopen"u8;

    /// <summary>
    /// Resolves party index <paramref name="partyIndex"/> (0 = this instance's own controlled
    /// PC/leader, 1-2 = the active companions in CSWParty order) to a CLIENT-side creature
    /// pointer, the same way <see cref="RotateControlToFront"/>'s callers and
    /// <c>Odyssey.TryGetPartyReferenceState</c> already do: <see cref="GetClientPlayerCreatureFn"/>
    /// for index 0, or CSWParty's own member array (<see cref="SwPartyMember0ClientId"/> +
    /// <paramref name="partyIndex"/> * <see cref="SwPartyMemberStride"/>, masked of
    /// <see cref="ControlPartyNpcFlag"/>) resolved through <see cref="GetCreatureByClientIdFn"/>
    /// for 1-2. Returns 0 (with <paramref name="detail"/> set) when the party has no live member
    /// at that index.
    /// </summary>
    private static nint ResolvePartyMemberCreature(nint client, int partyIndex, out string detail)
    {
        detail = string.Empty;

        if (partyIndex == 0)
        {
            nint pc = GetClientPlayerCreatureFn(client);
            if (pc == 0)
            {
                detail = "no client-controlled PC resolved";
            }

            return pc;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            detail = "no CSWParty resolved (client-side)";
            return 0;
        }

        // The client's CSWParty is a view, not a roster (memory: after a join the PC can sit
        // BEYOND the count, and live 2026-09-25 a joiner with one host companion read count 1).
        // Walk every slot the capacity allows and count companions, i.e. entries that are not
        // the PC, so index N means "the Nth companion" whatever order the view holds them in.
        nint controlled = GetClientPlayerCreatureFn(client);
        int liveCount = *(int*)(party + SwPartyMemberCount);
        int companionsSeen = 0;
        for (int slot = 0; slot < SwPartyMemberCapacity; slot++)
        {
            uint memberId = *(uint*)(party + SwPartyMember0ClientId + (slot * SwPartyMemberStride)) & ~ControlPartyNpcFlag;
            if (memberId == 0)
            {
                continue;
            }

            nint member = GetCreatureByClientIdFn(client, memberId);
            if (member == 0 || member == controlled)
            {
                continue;
            }

            if (++companionsSeen == partyIndex)
            {
                return member;
            }
        }

        // A joiner's client view can list no companions at all while its party table holds
        // them (live 2026-09-25: T3 in the party, CSWParty count 1, zero companions). Fall back
        // to the party table: the Nth active roster slot, translated server -> client id.
        if (TryReadPartyRoster(out _, out uint partyMask))
        {
            int rosterSeen = 0;
            for (int slot = 0; slot < 9; slot++)
            {
                if ((partyMask & (1u << slot)) == 0)
                {
                    continue;
                }

                uint serverId = GetPartyRosterObjectId(slot);
                if (serverId is 0 or ObjectInvalid || ++rosterSeen != partyIndex)
                {
                    continue;
                }

                uint clientId = ServerToClientObjectIdFn(client, serverId);
                nint member = clientId is 0 or ObjectInvalid ? 0 : GetCreatureByClientIdFn(client, clientId);
                if (member != 0 && member != controlled)
                {
                    return member;
                }
            }
        }

        detail = $"no companion #{partyIndex} in the client party view (CSWParty count {liveCount}, {companionsSeen} companion(s) found) or the party table";
        return 0;
    }


    /// <summary>
    /// Opens the Equipment screen (index 0 — <see cref="InGameGuiEquipScreenOffset"/>) on THIS
    /// instance showing party member <paramref name="partyIndex"/>, through the retail engine's
    /// own path: <see cref="ShowSWInGameGuiFn"/> (only when the full-screen GUI is not already
    /// showing Equipment — reopening an already-open screen is untested and unnecessary) and
    /// <see cref="SetCharacterFn"/> (always — the arrow-button call, so an already-open screen
    /// just switches subject). Dev-only / MAIN THREAD ONLY.
    /// </summary>
    internal static bool ShowEquipScreenForPartyMember(
        int partyIndex, out nint panel, out nint subject, out string detail)
    {
        panel = 0;
        subject = 0;
        detail = string.Empty;

        nint client = GetClient();
        nint gui = GetInGameGui();
        if (client == 0 || gui == 0)
        {
            detail = "no client / in-game GUI resolved (not in a loaded module)";
            return false;
        }

        if (*(int*)(gui + InGameGuiInitializedOffset) == 0)
        {
            detail = "the HUD is not initialised yet (still loading)";
            return false;
        }

        if (partyIndex is < 0 || partyIndex >= SwPartyMemberCapacity)
        {
            detail = $"partyIndex must be 0-{SwPartyMemberCapacity - 1} (got {partyIndex})";
            return false;
        }

        nint creature = ResolvePartyMemberCreature(client, partyIndex, out string resolveDetail);
        if (creature == 0 || !LooksLikeHeapPointer(creature))
        {
            detail = string.IsNullOrEmpty(resolveDetail)
                ? $"could not resolve a live creature pointer for party index {partyIndex}"
                : resolveDetail;
            return false;
        }

        int fullScreenBefore = *(int*)(gui + InGameGuiBusyOffset);
        int screenBefore = *(int*)(gui + InGameGuiCurrentScreenOffset);
        if (fullScreenBefore != 0 && screenBefore != 0)
        {
            // Live 2026-09-25: a freshly joined client read +0x30 = 1 with +0x2C = 7 and no screen
            // on show. That is the post-load Status Summary nobody clicked away, the same
            // leftover ShowGalaxyMapGui trips on. Restore the retail post-close input class
            // first, exactly as Odyssey.GalaxyMap does, then look again.
            if (!RestoreGameInputClass(out string restoreDetail))
            {
                detail = $"could not restore the game input class first: {restoreDetail}";
                return false;
            }

            fullScreenBefore = *(int*)(gui + InGameGuiBusyOffset);
            screenBefore = *(int*)(gui + InGameGuiCurrentScreenOffset);
        }

        bool equipmentAlreadyUp = fullScreenBefore != 0 && screenBefore == 0;

        // Another full-screen page reading as up is not a refusal: the retail Equip key does
        // the same ShowSWInGameGui call from any page and simply switches to Equipment. Live
        // 2026-09-25, a joiner kept reading +0x30 = 1 / +0x2C = 7 even straight after a
        // successful closegui, so refusing here made the verb unusable exactly where it is needed.

        if (!equipmentAlreadyUp)
        {
            fixed (byte* namePtr = GuiOpenGlobalName)
            {
                if (!TryWriteGlobal(
                        namePtr, GuiOpenGlobalName.Length, asBoolean: false, value: 0,
                        out _, out int afterGlobal, out string globalReason))
                {
                    detail = "could not set k_sup_guiopen (the screen-select global "
                        + $"ShowSWInGameGui reads): {globalReason}";
                    return false;
                }

                if (afterGlobal != 0)
                {
                    detail = $"k_sup_guiopen read back {afterGlobal} after being set to 0 - not "
                        + "declared as a Number global in this build; refusing rather than "
                        + "opening whatever screen it actually named";
                    return false;
                }
            }

            ShowSWInGameGuiFn(gui, 0);

            int fullScreenAfter = *(int*)(gui + InGameGuiBusyOffset);
            int screenAfter = *(int*)(gui + InGameGuiCurrentScreenOffset);
            if (fullScreenAfter == 0 || screenAfter != 0)
            {
                detail = "ShowSWInGameGui did not select the Equipment screen "
                    + $"(CGuiInGame+0x30 {fullScreenBefore}->{fullScreenAfter}, "
                    + $"+0x2C {screenBefore}->{screenAfter})";
                return false;
            }
        }

        nint equipPanel = *(nint*)(gui + InGameGuiEquipScreenOffset);
        nint manager = *(nint*)(gui + InGameGuiManagerOffset);
        if (equipPanel == 0 || !LooksLikeHeapPointer(equipPanel)
            || manager == 0 || GuiPanelExistsFn(manager, equipPanel) == 0)
        {
            detail = "the Equipment screen did not produce a registered panel";
            return false;
        }

        nint vtable = Deref(equipPanel);
        if (vtable != EquipScreenVtable)
        {
            detail = $"panel 0x{equipPanel:X} vtable 0x{vtable:X} is not CSWGuiInGameEquip "
                + $"(0x{EquipScreenVtable:X})";
            return false;
        }

        SetCharacterFn(equipPanel, creature);

        panel = equipPanel;
        subject = *(nint*)(equipPanel + EquipScreenSubjectOffset);
        detail = $"opened Equipment (was {(equipmentAlreadyUp ? "already up" : "closed")}) and "
            + $"set subject to party index {partyIndex} (creature 0x{creature:X})";
        return subject == creature;
    }
}
