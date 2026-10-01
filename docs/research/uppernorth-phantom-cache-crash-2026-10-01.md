# Upper City North phantom-cache crash

The genuine two-player Endar/Taris run reached the original Upper City North link on native96, SHA256 beginning `843148`, with matching Agent96. `usedoor UpperNorth` queued original engine walk-through movement from `tar_m02ac`; both native modules changed to `tar_m02ab`. Both games22760/28968 then exited at approximately 2026-10-01 00:03:43 UTC, shortly after their replacement phantom spawn lines. There was no rescue or combat fixture being driven during this transition.

All native logs, both exact 280-byte crash records, scoped Agent logs/manifest, entire current saves and personal sidecars are preserved in `artifacts/natural-campaign-v95/uppernorth-v96-crash-complete`. The healthy genuine Sarna-invitation checkpoint remains separately archived in `checkpoint-sarna-natural-invitation`, SHA256 `72CBBF30E8AC9CD471CB1F3531FB3B529D3B32DDBFD80DD4A51C0F8FAF21A3CD`. The attempted screenshot fell back to the desktop after the games exited and is not game evidence.

Matching immutable PDB and native instruction decoding identify both faults as plugin RVA `0x131658`, `PhantomController.ApplyAppearance`, at the raw appearance-field read. The instruction is `movzx edx, word ptr [ecx]`. The preserved stack supplies the nonzero body and `body+0xA60` field address: host `0x5EC06538`/`0x5EC06F98`, client `0x4E6C2538`/`0x4E6C2F98`. The shared return RVA `0x12E8AE` maps to the cached-body `PhantomController.OnFrame` drive call, not the initial spawn call. This is distinct from the separate Manaan engine effect-removal fault at `0x4FED74`.

The narrow patch validates the cached pointer against the current server object array, typed creature vtable, current ID, readable header and readable appearance field before either inactive-peer destruction or active drive. A stale slot is forgotten without destroying by its old ID, protecting a legitimate replacement occupant. Destruction independently checks identity; the spawn path checks current identity before and after its engine calls. Appearance reads use nonfaulting process-memory copies. The change adds no shared-block or protocol layout.

Focused phantom tests passed43/43 and Plugin Release/x86 built with warnings treated as errors, zero warnings/errors. These prove the decision rules and source compilation; live replay of the exact original transition is still required before claiming repair.

## Live replay, 2026-10-01 (three rounds)

Same checkpoint (`checkpoint-upper-city-before-cantina`), same `usedoor UpperNorth`, private game6 (host) / game7 (joiner). Evidence in `artifacts/natural-campaign-v95/uppernorth-v97*-replay`.

- **v97 (identity guard only):** no crash, but the peer never appeared. Every freshly spawned body was destroyed within about 5 ms and the controller respawned it about twice a second (roughly 150 times per game, in both areas).
- **v97b (added refusal logging):** every refusal showed `current 0x0`, header and appearance unreadable, vtable 0: the body's memory was already gone, so the guard was correct and something was destroying the body. The first death followed `triggers: fired OnEnter of 0x3F for peer 1` by 5 ms.
- **v97c:** `ScriptDestroyGuardHook` now also refuses a script's `DestroyObject` aimed at a body this plugin created for a peer (`NotePhantomBody`). Log: `script destroy guard: refused a script's DestroyObject on peer body 0xD7 (source 0x3B)` (one per game per crossing). Result: zero identity refusals, zero `forgot stale` lines, the transition completed on both games, the peer body rendered on both (Rig1/Rig2 labels), and a walk back through the link also survived with one phantom alive on each side.

So the original crash had two parts: a trigger or area script destroying the peer's stand-in body, and our cache then reading the freed body. Both are closed. The guard from the first patch stays as the backstop.

Not yet explained: after the walk back the joiner's body spawned at (221.22, 58.42) while the host's spawned at (187.71, 113.36). Both arrived at 187.7, 113.4 per `where`, so the joiner's body spawn position needs a look. Only this one crossing and one return were replayed; the Carth dialogue interruption seen once on the first attempt was not re-examined.
