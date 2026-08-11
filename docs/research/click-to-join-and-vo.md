# Click-to-join, and the voice-over gag — both root-caused

Two dialogue defects that had each survived several passes. Neither was an engine refusal;
both were the mod refusing itself.

## 1. Click-to-join: we annulled the click, one frame after it — FIXED

Three passes went looking for an engine guard rejecting the joiner. **There isn't one.**
All 22 refusal sites along `ActionInitiateDialog` → `HandlePlayerToServerInputMessage` →
`AIActionDialogObject` → `EventHandler` → `BeginConversation` were walked, and not one of
them can observe the peer's conversation. On the joiner its own copy of the NPC has
`obj+0x34 == NULL` and `dialog_owner (+0x54) == OBJECT_INVALID`; the joiner's server half
does not know the host is talking to anyone.

The refusal was `DialogueSpectateController.FollowRemote`, and the mechanism is a timing
trap:

1. `gui+0xB4` goes up **the instant the player clicks**, seconds before any conversation
   exists — `CSWCCreature::ActionInitiateDialog` (0x0060F620) calls `SetGlobalDialogState(1)`
   at 0x0060F76D, right after `SendPlayerToServerInput_Dialog`. And
   `CSWSObject::AIActionDialogObject` (0x0057A470) sets it *again* at 0x0057A70E — which is
   **before** its range test (0x0057A7AC / 0x0057A923) — re-queueing itself to the front for
   every tick the server spends walking the player into range.
2. Through that whole window `gui+0x170` / `gui+0x174` are still `OBJECT_INVALID`, so
   `ReadDialoguePartnerTag` publishes an **empty** `LocalDialoguePartnerTag`.
3. `PublishLocal` sets `_hadDialogue = true` — it only tests `ConversationLive`.
4. `FollowRemote` takes the `!_following && _hadDialogue` branch; `SameTag("", "end_trask")`
   is false by design; it falls to the yield-close, and `_nextYieldCloseMs` starts at 0, so
   it fires on the **first frame after the click**.
5. `CloseDialogue()` finds no resolvable participant and goes straight to
   `CGuiInGame::CloseDialog` (0x006332B0), which clears the flag at 0x00633423.

So the player was shown "click the same character to join", clicked it, and the mod cancelled
the request one frame later — and `DialogueGateHook` never ran at all, which is exactly why
`gui+0xB4` kept measuring 0 on the joiner and why gate-hunting kept finding nothing.

Why the *adopt* path always worked (~880 ms, measured earlier) while click-to-join never did:
two players in the same trigger get the conversation with no click and no walk, so the flag
and the participants come up together and the tag is non-empty on the first frame.
Click-to-join has a seconds-long window where the tag is empty. Adopt can never lose it;
click can never win it.

**Fix:** an unidentified local conversation is not a competitor. `FollowRemote` now waits for
`LocalDialoguePartnerTag` to resolve (grace = `FollowOpenTimeoutMs`) before treating a live
local conversation as something to yield. The moment the tag resolves, the existing `SameTag`
decision runs unchanged, so a genuine competing conversation is still yielded at the first
instant it is identifiable.

**Verified live**: `same NPC joins on click: PASS`, and the joiner's log reads
`engine reached StartDialog` → `adopting it to follow` → `delivered content 0 ms after opening`.

Two smaller findings from the same read:
- The "unstick a stale conversation shell" branch is **unreachable**: it sits behind
  `!_hadDialogue`, and nothing runs the engine between that read and the
  `Odyssey.IsConversationActive` check a few statements later. It has never executed.
- `CGuiInGame+0x188` (the object `MainLoop` ticks the dialog on) is written only by
  `CSWSObject::StartDialog` at 0x004CF7A6 and **never cleared**, so `DialogueGateHook`'s
  refusal path leaves it aimed at the previous conversation's owner.

## 2. The voice-over gag: `SetCanSendDialog(true)` is not a restore — FIXED

`CClientExoAppInternal::SetCanSendDialog` (0x005F3030, `ret 4`) writes the flag at `+0x228`
**and stamps `+0x22C` with the current world time** (0x005F305F).
`GetCanSendDialog` (0x005F3070, `ret 0xC`) then refuses while `now - [+0x22C] <= 1000`
(0x005F316C `jbe` → 0x005F32C8 `xor eax,eax; ret 0xC`), and on success back-dates the stamp
to `now-2000` so the gate holds itself open. The engine re-stamps it only during area load
(`MainLoop` 0x006031DB, inside the `area_not_ready_to_display` branch at 0x00602EFC).

While that gate is shut, `CSWSObject::IsDialogDelay` (0x004CD2D0) returns 1 and
`CSWSObject::SendDialogEntry` (0x004CD460) **defers** the entry instead of calling
`CSWSDialog::SendDialogEntry` — the only producer of `CGuiInGame::HandleDialogSound`
(0x0062E1D0, the voice-over) and of the lipsync arming that follows it.

So each of the three `SetCanSendDialog(true)` calls was a fresh ~1 second of silence, not a
restore. Nothing sets the flag false any more, so there was nothing to restore. All three are
deleted; `Odyssey.SetCanSendDialog` is kept with a DO-NOT-CALL banner.

### The line-advance path is NOT the problem

Subtitles and voice-over come from the *same* function with no branch between them:

```
CSWSDialog::SendDialogEntry 0x005A4010   (ret 0x10)
  0x005A4341 -> SendDialogEntryNode -> HandleDialogEntry -> ShowDialogEntry   <- subtitle
                                       0x00632042  mov [gui+0x170], edi       <- speaker
  0x005A4439 -> CGuiInGame::HandleDialogSound 0x0062E1D0                      <- voice-over
```

Single-caller at every hop. So "the follower is frozen and never advances" is dead: the VO
call *is* entered and bails inside itself.

### The remaining suspect, and how to settle it

`HandleDialogSound` has three bails. Two are excluded by the symptom — a failed stream still
calls `LipSync` at 0x0062E2D6 (mouth would move silently), and an empty `sound` resref is
content identical on both installs. That leaves **0x0062E204: `GetGameObject(*(gui+0x170))`
returns 0 — the entry's speaker has no client-side object on the follower.**

Not yet proven, and deliberately not asserted. The probe is a watch hook on 0x0062E1D0
recording raw `gui+0x170` per line: `0x7F000000` means the speaker was never resolved
server-side (`CSWSDialog::GetSpeaker` tag lookup, plausibly interacting with the solo mode
the joiner holds); `0x8xxxxxxx` with no client object means the twin is missing.

## Corrections to `dialogue-sync.md`

- `CSWSObject::UpdateDialog` (0x004CD580) passes **only** when `dialog_owner (+0x54)` equals
  the object's own id `(+4)`; it returns 0 when `dialog_owner == OBJECT_INVALID`. The doc has
  this backwards.
- `CGuiInGame::HandleDialogEntry` (0x00631D80) writes `gui+0x170` (speaker) / `+0x174`
  (listener) unconditionally, shifting the previous pair into `+0x178` / `+0x17C`.
- `ServerToClientObjectId` is `id | 0x80000000`, not a table lookup.
- `CGuiInGame+0xBDC` / `+0xBE0` are the lipsync target id and its sound resref.
- `CExoStreamingSoundSource::Play` (0x005D5B10) is listed with `param_size_bytes = 0` but
  both call sites push **five** dwords. Trust the binary.
