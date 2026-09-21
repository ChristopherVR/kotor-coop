# Difficulty agreement review (2026-09-21)

The review item says the difficulty setting is per instance and never compared. The shipped
retail image gives two different facts that must stay separate:

* `CSWSCreature::DoDamage` reads the client-options difficulty byte at `CClientOptions + 4`
  for party members and the PC. That byte is the in-game slider and is persisted as
  `Difficulty Level` in `swkotor.ini` (`docs/research/min-one-hitpoint.md` §3).
* The same routine skips that multiplier for non-party enemies. The enemy damage channel
  therefore remains symmetric for an enemy owner and its mirrored copy; the receiver runs
  the same raw enemy damage through its own engine pipeline (`docs/research/peer-damage-ledger.md`
  §1.2–§2).

There is no shipped read of `CClientOptions + 4` in the agent or plugin, and the slider can
change while a session is running. Adding a guessed INI value to the install digest would
therefore produce stale or false agreement. The safe implementation boundary is a future
read-only, runtime sampled field with an explicit unknown state, followed by a warning-only
comparison. No difficulty write, host adoption, or damage rescaling is justified by the
current evidence. The existing `MinHP1` probe is separate: retail loads server difficulty 2
(`normal`), where that table cell is empty.
