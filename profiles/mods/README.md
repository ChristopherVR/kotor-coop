# Checked-in mod compatibility profiles

Add one JSON file per mod here. Packaging installs these files in `coop-mods/`
beside `swkotor.exe`. Only profiles whose detection files all exist activate;
shipping a profile does not install the mod. Restart both games and agents after
changing a profile. Both players need identical active profile bytes and mod files.

```json
{
  "schema": 2,
  "id": "example-campaign",
  "name": "Example campaign",
  "status": "experimental",
  "detect": { "allFiles": ["modules/example.mod", "Override/example_ally.utc"] },
  "campaignModules": ["example_*"],
  "holdUnmappedSlots": [0],
  "companions": [
    {
      "slot": 0,
      "template": "example_ally",
      "when": { "global": "EXAMPLE_PHASE", "min": 30, "max": 30 }
    }
  ],
  "notes": ["Fictional example; verify against the mod's recruitment scripts."]
}
```

- `id` must be unique across the catalogue and optional legacy `coop-mods.json`.
- `detect.allFiles` contains exact relative file paths under `Override/` or
  `modules/`. Traversal and absolute paths are rejected. Detection proves presence,
  not package version or qualification; the content audit compares actual files.
- `companions` maps engine slots 0–8 to UTC resource names (no extension, at most
  16 ASCII letters, digits or underscores). It controls automatic recovery of a
  missing companion, rather than replacing the mod's own recruitment scripts.
- Optional `module` selects an exact module or trailing `*` prefix; omitted means
  `*`. Exact matches win, followed by the longest prefix. Conflicting rules are
  rejected, including conflicts between active mods.
- Optional `when` reads a numeric story global. `min` and `max` are inclusive
  signed-byte bounds (-128 through 127). Missing or unreadable globals hold recovery.
  A false condition alone does not prevent vanilla fallback outside campaign
  modules. Use `holdUnmappedSlots` for slots whose identity can change before a
  synchronized global arrives.
- `recovery: "hold"` can replace `template` for an explicit rule that must wait
  for native scripts. Unmapped slots in `campaignModules` also wait.
- `holdUnmappedSlots` holds those slots in every module unless a mapping resolves.
  This is deliberately conservative and can delay stock companion recovery too.
- Metadata `name`, `status`, `package`, `source`, `$schema` and `notes` does not
  change runtime policy. Unknown runtime fields fail validation rather than silently
  pretending to implement a fix. Invalid profiles hold automatic template recovery.

Run `KotorCoop.Agent.exe mods --game-path <game> --compare-with <peer-game>` before
live testing. Inactive profiles do not affect content fingerprints. The legacy
single-file schema 1 remains accepted for personal configurations.

Keep mod assets and downloaded packages out of this directory. Check in the small
compatibility declaration, script evidence and test results. Mark a campaign
supported only after recruitment, dismissal, transitions, forced scenes, combat,
temporary player characters, save adoption and reconnect have been exercised.
The Solomon profile is currently experimental; see the isolated test report in
`docs/research/solomon-isolation-2026-09-27.md`.
