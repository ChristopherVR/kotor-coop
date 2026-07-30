# Phantom name plates — solved (research 2026-07-31)

The earlier "AddString ran but no text found" mystery is closed. Two separate facts:

1. **The server-side write was correct all along**; the text was five indirections deep:
   `stats+0x34 CExoLocString {internal*, strref@+4}` → internal `{list*, count@+4, fromTlk@+8}`
   → list `{head, tail, count}` → node `{prev, next, data@+8}` → entry
   `{id@0 (=2*lang+gender), char* text@+4, capacity@+8}`. Trask's "empty" locstring was
   never evidence against substrings: his name lives in the STRREF at `stats+0x38`
   (first) / `stats+0x40` (last); `ClearLocString` never touches the strref.
2. **The selection band never reads any of that after the twin exists.** It renders
   client-side: `CSWGuiTargetActionMenu::UpdateNameLabel 0x00685CB0` → twin vtable `+0x70`
   = `CSWCCreature::GetName 0x00617090` → plain `CExoString`s at
   `[[creature+0x224]+0x2F8]+0x14` (first) / `+0x1C` (last). Those are populated exactly
   once — the object's FIRST GameObjUpdate (CreateNewLastUpdateObject 0x00577000 sets all
   mask bits) — because the per-frame diff `ComputeAppearanceUpdateRequired 0x00567100`
   NEVER sets name bit `0x400` (decoded end to end). Names are transmitted once, ever.

## The fix (implement in Odyssey.SetCreatureFirstName + PhantomController)

Keep the server half (ClearLocString + AddString(0, &s, 0) — feeds saves/feedback/
GetFullName), and ADD the client half, which is load-bearing:

```
twin = [creature + 0x224]
if twin == 0: fine — the first GameObjUpdate will carry the name
guard: [twin] == 0x0074F5F8 (CSWCCreature vtable)
CSWCCreature::SetName 0x0060D600  __thiscall(this, CExoString* first, CExoString* last) ret 8
  — pass non-null empty ("") last name, engine copies with its own allocator
```

Re-assert periodically (once/second) and after module transitions — the twin is
recreated and only gets the name if the server locstring is already set at that moment.
Fallback if SetName misbehaves: assign `clientStats+0x14`/`+0x1C` directly via
`CExoString::operator=_2 0x005E5C50`.

**TLK is NOT needed and must not be used**: substrings beat strrefs in GetString_3
(0x005EA130), WriteCExoLocStringServer transmits substrings as literal text, and TLK
text is read from the FILE on every fetch (no resident table to patch). Mod-safe by
construction.

## Verification (cheap, memory-only)

- Server half landed: walk the chain, expect count 1, id 0, text = name.
- Band will show it: read `[[creature+0x224]+0x2F8]+0x14` char* — that IS the band text.
- Trask control: `[TraskStats+0x38]` is a valid strref (resolvable via CTlkTable::Fetch
  0x0041E550, this=[0x007A3A08]).

## Handoff corrections to fold in

- 4.11's "set before the twin exists so it's created with the name" is unreliable —
  the twin write is required.
- CExoString `+0x04` is CAPACITY (strlen+1), not length; operator=_2 strlen()s the
  source and ignores source capacity.
- New offsets: CSWSCreatureStats first_name +0x34/strref +0x38/last_name +0x3C/strref
  +0x40; CSWCCreatureStats first +0x14/last +0x1C via [[creature+0x224]+0x2F8];
  CSWCCreature vtable +0x70 = GetName.
