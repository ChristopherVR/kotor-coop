# The engine API the plugin calls, verified against the binary

Every place the co-op plugin enters the Odyssey engine, checked against the function actually
living at that address in GOG `swkotor.exe` v1.0.3. Regenerate with:

```
python tools/callaudit.py --markdown
python tools/callaudit.py --bad        # exits non-zero if anything disagrees
```

## Why this exists

The plugin reaches the engine through raw function pointers — a
`delegate* unmanaged[Conv]<args..., ret>` cast over a hardcoded address — and nothing checked
those declarations against reality. The failure mode is the worst available.

A `__thiscall` or `__stdcall` callee **pops its own arguments**. Declare one argument too many
and every call leaves ESP four bytes off. That does not fault where it happens: the stack
pointer drifts, and some unrelated function returns into a corrupted frame later. What you see
is `STATUS_STACK_BUFFER_OVERRUN` (0xC0000409) somewhere with no connection to the real cause —
which is exactly the intermittent fast-fail this project had been logging at phantom-spawn
time without an explanation.

So the oracle is the callee's own epilogue, which cannot be argued with: a function that
cleans its own stack ends in `ret imm16`, and that immediate **is** the argument size in
bytes. A plain `ret` means it takes none. The `this` pointer travels in ECX and is not
counted, so a thiscall declared `<nint, A, B, ret>` must meet `ret 8` — not `ret 0xc`.

## What it found

Two wrong declarations, both real:

| Function | Declared | Actually | Consequence |
| --- | --- | --- | --- |
| `CSWSWaypoint::RemoveFromArea 0x005C8350` | `(this, area)` | `(this)` — plain `ret` at 0x005C83A8, area derived from `this` via 0x004CB120 | **Called.** Four bytes leaked per map-marker teardown. |
| `CClientExoApp::SetPauseState 0x005ED650` | `(this, int, int)` | `(this, int, int, int)` — `ret 0xc` at 0x005F8285, third argument read at 0x005F822A | Not called (marked DO NOT CALL), but the comment explaining a 2026-08-01 crash blamed a notify-list walk alone. The signature was also wrong. |

## Three ways to get the epilogue wrong

Each was got wrong first, and each produced confident nonsense — worth recording, because the
naive version of this tool is worse than no tool:

1. **A byte scan for 0xC2/0xC3.** Those bytes appear constantly inside displacements and
   immediates; it reported a callee popping 15116 bytes.
2. **Stopping at a forwarding thunk.** A large share of engine entry points are
   `mov ecx, [ecx+4]; jmp impl`, adjusting `this` to a base subobject. The stack cleanup is
   the target's. Stopping at the thunk decodes through the alignment padding into whatever
   function is laid out next and reports *its* epilogue — 18 false mismatches, every one a
   thunk. (Same hazard as the adjustor thunks in `docs/research/animation-sync.md`: call the
   slot, not the address.)
3. **Treating every `jmp` as a tail call.** A `jmp` deeper in a body is an ordinary branch,
   and `jmp dword ptr [reg*4 + table]` is a switch whose arms follow inline. Following those
   burned the hop budget and lost four functions that decode perfectly well.

## The symbol DB, measured against this binary

`tools/kotor1_0_3.db` carries 9,710 functions and 4,720 struct offsets, and everything in this
project consults it — but it was imported from another build's symbols and nothing had ever
confirmed it describes *this* executable. A wrong row is worse than a missing one: it is the
source you trust when writing the declaration that then corrupts the stack.

`python tools/dbaudit.py` compares every row against the callee's own epilogue:

| | count |
| --- | --- |
| agree with the binary | **5,399** |
| **disagree** | **310** |
| `param_size_bytes` unpopulated (0) | 3,188 |
| size not a whole number of stack slots | 803 |
| epilogue unreadable | 1 |

So 5,399 functions are now positively confirmed against the GOG build, and 310 specific rows
are known not to be trustworthy. Both halves are useful; the second is the point.

⚠ Two readings of the data that look like findings and are not. The naive comparison reports
1,422 disagreements, and most of that is the tool being wrong:

* **`param_size_bytes` is not the stack size.** It sums the raw `sizeof` of each declared
  parameter, so `CSWSCreatureStats::SetSTRBase(int)` reads 1 and `HasFeat(short)` reads 2,
  while the stack gives every argument a 4-byte slot. Almost every spurious disagreement was
  off by exactly +3 or +2 — the rounding. Only multiple-of-4 rows are compared.
* **Zero means unknown, not "no arguments".** 336 of the remaining disagreements were size 0
  against a callee that plainly pops something, `CResRef::operator==` among them — which
  cannot take no arguments. The column is simply unpopulated for a third of the DB. An
  unpopulated row is a gap, and counting it as an error buries the rows that really are wrong.

## The verified surface

| Address | Symbol | Conv | Args | Callee pops | Declared in |
| --- | --- | --- | --- | --- | --- |
| `0x00401380` | `CAppManager::CreateServer` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x00401C10` | `Global::UpdateScreen` | Cdecl | 3 | `ret 0` | MenuLoadController |
| `0x00401E00` | `Global::ActivateRenderWindow` | Stdcall | 0 | `ret 0` | Odyssey |
| `0x0040BC70` | `CSWGuiManager::AddPanel` | Thiscall | 4 | `ret-imm 12` | MenuLoadController, NativeBrowserController, PasswordEntryModal |
| `0x0040BD70` | `CSWGuiManager::PanelExists` | Thiscall | 2 | `ret-imm 4` | NativeBrowserController, PasswordEntryModal |
| `0x0040BE00` | `CSWGuiManager::PopModalPanel` | Thiscall | 1 | `ret 0` | MenuLoadController |
| `0x00414C00` | `CSWGuiBorderParams::SetFillImage` | Thiscall | 3 | `ret-imm 8` | NativeBrowserController |
| `0x00415E00` | `CSWGuiTextParams::SetText` | Thiscall | 2 | `ret-imm 4` | NativeBrowserController, Odyssey, PasswordEntryModal |
| `0x00416490` | `CSWGuiEditText::SetText` | Thiscall | 2 | `ret-imm 4` | PasswordEntryModal |
| `0x00418D80` | `CSWGuiButton::Initialize_2` | Thiscall | 3 | `ret-imm 8` | NativeBrowserController |
| `0x0041AB20` | `CSWGuiControl::AddEvent` | Thiscall | 4 | `ret-imm 12` | Odyssey |
| `0x0041C040` | `CSWGuiListBox::SetSelectedControl` | Thiscall | 3 | `ret-imm 8` | NativeBrowserController |
| `0x0041C1D0` | `CSWGuiListBox::AddControls` | Thiscall | 5 | `ret-imm 16` | NativeBrowserController |
| `0x0044D490` | `Global::AurPostString` | Cdecl | 4 | `ret 0` | Odyssey |
| `0x004ADFF0` | `CWorldTimer::PauseWorldTimer` | Thiscall | 1 | `ret 0` | MenuLoadController, Odyssey |
| `0x004AE030` | `CWorldTimer::UnpauseWorldTimer` | Thiscall | 1 | `ret 0` | MenuLoadController, Odyssey |
| `0x004AE6B0` | `CServerExoApp::GetModule` | Thiscall | 1 | `ret 0` | MenuLoadController, Odyssey |
| `0x004AE750` | `CServerExoApp::GetGameObject` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004AE860` | `CServerExoApp::MainLoop` | Thiscall | 1 | `ret 0` | MenuLoadController |
| `0x004AE9A0` | `CServerExoApp::SetPauseState` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x004AE9F0` | `CServerExoApp::GetDifficultyOption` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004AECC0` | `CServerExoApp::SetMoveToModulePending` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004AECD0` | `CServerExoApp::SetMoveToModuleString` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004AED30` | `CServerExoApp::SetMoveToModuleStartWaypoint` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004AED70` | `CServerExoApp::GetObjectArray` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x004AEDE0` | `CServerExoApp::GetWorldTimer` | Thiscall | 1 | `ret 0` | MenuLoadController, Odyssey |
| `0x004AEE70` | `CServerExoApp::GetPartyTable` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x004AEFA0` | `CServerExoApp::ClearAllQueuedCombatActions` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004AEFC0` | `CServerExoApp::GetFactionManager` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x004AF090` | `CServerExoApp::SetDoingStartNewModule` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x004B2820` | `CServerExoApp::GetPlayerCreature` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x004CB4F0` | `CSWSObject::StopDialog` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x004CC250` | `CSWSObject::ClearDirtyFlag` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004CC280` | `CSWSObject::SetCurrentHitPoints` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004CCD80` | `CSWSObject::ClearAllActions` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004CD170` | `CSWSObject::SetOrientation` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004CD1A0` | `CSWSObject::SetPosition` | Thiscall | 5 | `ret-imm 16` | Odyssey |
| `0x004CD2D0` | `CSWSObject::IsDialogDelay` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x004ED850` | `CSWSCreature::UpdateAppearanceDependantInfo` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004EF890` | `CSWSCreature::GetDeadTemp` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x004EFEF0` | `CSWSCreature::SetAmbientAnimationState` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x004F2610` | `CSWSCreature::SetCombatState` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x004FD960` | `CSWSCreature::PacifyCreature` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x00513FC0` | `CSWSCreature::GetFaction` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x00516630` | `CSWSCreature::WalkUpdateLocationDistance` | Thiscall | 5 | `ret-imm 16` | Odyssey |
| `0x0052B3B0` | `CFactionManager::GetFaction` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x0052DE70` | `CSWVirtualMachineCommands::ExecuteCommandAddPartyMember` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x0052F820` | `CSWVirtualMachineCommands::ExecuteCommandCreateObject` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x00541C00` | `CSWVirtualMachineCommands::ExecuteCommandRemovePartyMember` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x00543ED0` | `CSWVirtualMachineCommands::ExecuteCommandSpawnAvailableNPC` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x00563CF0` | `CSWPartyTable::ResetCurrentSessionStartTim` | Stdcall | 0 | `ret 0` | MenuLoadController |
| `0x00565B00` | `CSWPartyTable::PositionParty` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x00578ED0` | `CSWSAreaMap::GetMapRotateCCWFromWorldOrientation` | Thiscall | 4 | `ret-imm 12` | Odyssey |
| `0x005790C0` | `CSWSAreaMap::SetPartyMemberWorldLocation` | Thiscall | 5 | `ret-imm 16` | Odyssey |
| `0x005791B0` | `CSWSAreaMap::GetPartyMemberMapLocation` | Thiscall | 4 | `ret-imm 12` | Odyssey |
| `0x00584AC0` | `CSWSPlaceable::RemoveItem` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005890D0` | `CSWSDoor::GetIsLinked` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x00589600` | `CSWSDoor::SetOpenState` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x00589680` | `CSWSDoor::MoveToNextOpenState` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x00589C70` | `CSWSDoor::OpenDoor` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x00590090` | `CSWSEncounter::GetInActivateArea` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x00593BE0` | `CSWSEncounter::SpawnIfAppropriate` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x005A4010` | `CSWSDialog::SendDialogEntry` | Thiscall | 5 | `ret-imm 16` | Odyssey |
| `0x005BFA70` | `CSWSFaction::AddMember` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x005C5A40` | `CSWSJournal::SetState` | Thiscall | 4 | `ret-imm 12` | Odyssey |
| `0x005C7E70` | `CSWSWaypoint::Constructor` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005C8350` | `CSWSWaypoint::RemoveFromArea` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005C8580` | `CSWSWaypoint::AddToArea` | Thiscall | 6 | `ret-imm 20` | Odyssey |
| `0x005D1000` | `CVirtualMachine::StackPopInteger` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005D1010` | `CVirtualMachine::StackPushInteger` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005D1030` | `CVirtualMachine::StackPushFloat` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005D1090` | `CVirtualMachine::StackPushString` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005D10B0` | `CVirtualMachine::StackPushEngineStructure` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x005D10C0` | `CVirtualMachine::StackPopObject` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005D10D0` | `CVirtualMachine::StackPushObject` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005D5040` | `CNetLayer::StartConnectToSession` | Thiscall | 6 | `ret-imm 20` | MenuLoadController |
| `0x005D57D0` | `CNetLayer::ProcessReceivedFrames` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x005DF4E0` | `CExoInput::ClearEvents` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005DF520` | `CExoInput::KeyboardModeOn` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005E5A90` | `CExoString::CStrConstructor` | Thiscall | 2 | `ret-imm 4` | MenuLoadController, NativeBrowserController, Odyssey, PasswordEntryModal |
| `0x005E5C20` | `CExoString::Destructor_2` | Thiscall | 1 | `ret 0` | NativeBrowserController, Odyssey, PasswordEntryModal |
| `0x005E9EA0` | `CExoLocString::ClearLocString` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005EA340` | `CExoLocString::AddString` | Thiscall | 4 | `ret-imm 12` | Odyssey |
| `0x005ED420` | `CClientExoApp::DisplayMainMenu` | Thiscall | 2 | `ret-imm 4` | NativeBrowserController |
| `0x005ED430` | `CClientExoApp::UnloadModule` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005ED490` | `CClientExoApp::GetClientName` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x005ED4F0` | `CClientExoApp::GetWorldTimer` | Thiscall | 1 | `ret 0` | MenuLoadController, Odyssey |
| `0x005ED530` | `CClientExoApp::GetModule` | Thiscall | 1 | `ret 0` | MenuLoadController, Odyssey |
| `0x005ED540` | `CClientExoApp::GetPlayerCreature` | Thiscall | 1 | `ret 0` | MenuLoadController, Odyssey |
| `0x005ED580` | `CClientExoApp::GetGameObject` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005ED650` | `CClientExoApp::SetPauseState` | Thiscall | 4 | `ret-imm 12` | Odyssey |
| `0x005ED690` | `CClientExoApp::GetInGameGui` | Thiscall | 1 | `ret 0` | MenuLoadController |
| `0x005ED6F0` | `CClientExoApp::GetSWCMessage` | Thiscall | 1 | `ret 0` | MenuLoadController, Odyssey |
| `0x005ED730` | `CClientExoApp::InitializeInGameGUI` | Thiscall | 1 | `ret 0` | MenuLoadController |
| `0x005ED8B0` | `CClientExoApp::GetSWParty` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005EDA20` | `CClientExoApp::TurnOffKeyboardModal` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005EDA60` | `CClientExoApp::SetInputClass` | Thiscall | 3 | `ret-imm 8` | MenuLoadController |
| `0x005EDA80` | `CClientExoApp::ShowLoadScreen` | Thiscall | 4 | `ret-imm 12` | MenuLoadController |
| `0x005EDA90` | `CClientExoApp::HideLoadScreen` | Thiscall | 3 | `ret-imm 8` | MenuLoadController |
| `0x005EDAE0` | `CClientExoApp::SetAreaNotReadyToDisplay` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x005EDB40` | `CClientExoApp::IsMoviePlaying` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005EDB50` | `CClientExoApp::AddMoveToModuleMovie` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005EDBE0` | `CClientExoApp::DisableInput` | Thiscall | 1 | `ret 0` | MenuLoadController |
| `0x005EDBF0` | `CClientExoApp::EnableInput` | Thiscall | 1 | `ret 0` | MenuLoadController |
| `0x005EDC80` | `CClientExoApp::SetCanSendDialog` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x005EDCF0` | `CClientExoApp::SetLoadScreenByModuleName` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x005EDD30` | `CClientExoApp::SetLoadMusic` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x005EDD40` | `CClientExoApp::SetLoadBarProgress` | Thiscall | 3 | `ret-imm 8` | MenuLoadController |
| `0x005EE380` | `CClientExoApp::RemoveMoveToModuleMovies` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x005F2C90` | `CClientExoAppInternal::BarkString` | Thiscall | 5 | `ret-imm 16` | Odyssey |
| `0x0060D600` | `CSWCCreature::SetName` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x0060F620` | `CSWCCreature::ActionInitiateDialog` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x006183D0` | `CSWCCreature::AddFireForgetAnimation` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x0062AA00` | `CGuiInGame::SetSWGuiStatus` | Thiscall | 3 | `ret-imm 8` | MenuLoadController |
| `0x0062B0A0` | `CGuiInGame::InitializeMap` | Thiscall | 1 | `ret 0` | MenuLoadController |
| `0x0062B410` | `CGuiInGame::SetGlobalUnpauseableState` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x0062CBA0` | `CGuiInGame::HideSWInGameGui` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x0062DEF0` | `CGuiInGame::SetPaused` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x006332B0` | `CGuiInGame::CloseDialog` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x00635350` | `CSWParty::IsPartyTogether` | Thiscall | 1 | `ret 0` | Odyssey |
| `0x0063C300` | `CSWCObject::AnimationLooping` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x0063C380` | `CSWCObject::AnimationFireAndForget` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x0063C410` | `CSWCObject::AnimationOverlay` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x0063F9D0` | `CSWCModule::SetCameraFollow` | Thiscall | 3 | `ret-imm 8` | Odyssey |
| `0x006775E0` | `CSWCMessage::SendPlayerToServerStatus_RequestStatus` | Thiscall | 1 | `ret 0` | MenuLoadController |
| `0x00677D10` | `CSWCMessage::SendPlayerToServerInput_UseObject` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x00677EA0` | `CSWCMessage::SendPlayerToServerInput_Dialog` | Thiscall | 2 | `ret-imm 4` | Odyssey |
| `0x006CA250` | `CSWGuiSaveLoad::LoadPCAutoSave` | Thiscall | 2 | `ret-imm 4` | MenuLoadController |
| `0x006CAE70` | `CSWGuiSaveNamePanel::Constructor` | Thiscall | 2 | `ret-imm 4` | PasswordEntryModal |
| `0x006CB940` | `CSWGuiSaveLoadEntry::Constructor` | Thiscall | 1 | `ret 0` | NativeBrowserController |
| `0x006CC680` | `CSWGuiSaveLoad::Constructor` | Thiscall | 4 | `ret-imm 12` | MenuLoadController, NativeBrowserController |
| `0x006FA7E6` | `Global::operator_new_2` | Cdecl | 1 | `ret 0` | MenuLoadController, NativeBrowserController, Odyssey, PasswordEntryModal |
