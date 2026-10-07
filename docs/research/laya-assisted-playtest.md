# Laya-assisted playtesting: can it replace the human gates?

Append-only. Question from the owner (2026-10-08): the release gates in
[RELEASE-READINESS.md](../RELEASE-READINESS.md) that "need people" — can Laya do them?

## 2026-10-08: assessment

### What Laya is here

Laya 0.3.28 is a local model behind the `laya-kotor-pilot` stdio MCP server that Codex
registered on 2026-10-07 (venv and checkpoints under the git-ignored `artifacts/laya-pilot/`).
Its eight tools (`laya_status`, `laya_route`, `laya_predict`, `laya_predict_batch`,
`laya_route_batch`, `laya_shortlist`, `laya_preset`, `laya_decide`) take a **text/JSON state**
and a question with named choices, and return a choice plus a confidence and an abstention
flag. CPU only, about 400 ms per call warm. It has **no image input**.

`tools/laya_playtest.py` (Codex, uncommitted at the time of writing) wraps it: it reads the
host's engine state out of process (module, position, nearby creatures/doors, conversation and
reply count), builds one to three legal actions with fixed rules (`candidates()`), asks Laya to
pick one, and executes it through the plugin's console mailbox (`input`, `attack`, `talktag`,
`opendoor`, `usedoor`, `walkto`, `screenshot`).

### What the evidence says

- Classification smoke (`artifacts/laya-pilot/report.txt`): 3 of 5 labelled cases right on both
  checkpoints; it called a desync and a stuck player "healthy". A 0.7 confidence gate flagged
  the wrong answer but still returned `choice=healthy`, so a consumer that reads only `choice`
  is misled.
- The 22-step "clean playthrough" (`artifacts/laya-pilot/evidence/clean-playthrough/report.json`)
  never left `end_m01aa`. Every step Laya "decided" had a single sensible option (press Space to
  continue a voiced line, or one numbered reply with no reply text available); the other five
  steps fell back to the fixed rules. The rule generator did the playing; Laya added no
  information.

### What the human gates actually need

The open P0 human items (named save, dialogue over the host's equipment menu, Trask's
recruit/switch/door, subtitles and skipping, the enemy-sighted banner, manual pause on both
peers) need two things:

1. **Real retail input**, through the same paths a keyboard and mouse use.
2. **Judging what is on screen**: a dialogue overlay, a subtitle, a banner, the right body.

Laya cannot do (2) at all. (1) is not a model problem. The real gap was that the plugin's
own polled keys (`NativeMethods.IsKeyDownHere`: Enter-to-chat, the pause key, F9, console Tab,
spectate cycling) read `GetAsyncKeyState` on a foreground window, which the ghosted rig windows
never are, so those paths could only be pressed by a person.

### What was done instead (and worked)

- `IsKeyDownHere` now also honours a key held by `input key <dik> down` when
  `isolate_real_input` is on (rig only; `NativeMethods.RigKeyHeld`, set at startup). Real
  players are unaffected.
- The screen is judged by a multimodal reviewer (Claude reading the plugin's `screenshot <name>
  [front]` BMPs). Note the back buffer reads black on some full-screen GUIs; retake with `front`.
- Engine state is the oracle wherever one exists: the save folder and its `savenfo.res`
  `SAVEGAMENAME`, `tools/pausestate.ps1`'s mask, positions from `laya_playtest.py --snapshot`.

Results on the rig, 2026-10-08, merged main:

- **Named save, return to play, enemy-sighted panel.** Esc → Save Game → New Slot → Save →
  `input text ClaudeSave1` → `input char enter` landed `000901 - Game900` with
  `SAVEGAMENAME = ClaudeSave1`; Cancel/Close returned to play with a hostile Sith soldier in
  view, no ENEMY SIGHTED panel, pause mask 0.
- **Enter is not stolen by chat in the save-name popup.** `input key 0x1C down` +
  `input char enter` + `input key 0x1C up` (both halves of a physical press) saved
  `000902 - Game901` and opened no `say` box. Control: the same held Enter in play opened the
  `say` box.
- **Minigame hold with the new `gamepad = 0` default.** Host `setglobal K_RANDOM_MINI_GAME true`
  + `warp m12ab`; the joiner logged `minigame lock: a peer is racing`, and two seconds of held W
  moved it 0.000 units. After the host returned (`minigame lock: the minigame ended`) the same
  input moved it about 10 units.

### Recommendation

Do not use Laya as a pass/fail judge or as the player for the human gates. Script each gate as
real-input steps with engine-state assertions, capture screenshots at each step, and have a
multimodal reviewer look at them. Laya could still be useful later for **exploration**
(choosing among many genuinely different legal actions over long runs to find crashes), once
the candidate generator offers real alternatives and reply text is exposed. Even then, measured
checks own PASS/FAIL.

What still needs a person: whether things look and feel right in motion (animation, camera,
subtitle timing with real sound), a second real PC and network, and the full campaign.
