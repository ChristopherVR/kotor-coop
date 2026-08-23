namespace KotorCoop.Plugin;

/// <summary>
/// K1's nine party-roster slot -&gt; story-companion-blueprint mapping, split out of
/// <see cref="Odyssey.AddAvailableNpcByTemplate"/> so the table itself can be unit-tested — the
/// same split <see cref="PartyRosterRecruitRule"/> uses for the decision half of the same
/// feature; the engine call itself does not exist in the test runner, but this data does not
/// need one.
///
/// **Confidence, round 2026-08-24**: every entry below is now independently STATICALLY VERIFIED
/// (not just "standard nwscript.nss declaration order" asserted by convention), by disassembling
/// the GOG install's own shipped `.ncs` scripts (`tools/kotorfiles.py` archive readers +
/// `tools/xrefs.py`/`tools/disasm.py` against `swkotor.exe`, no live game touched this round; see
/// docs/research/dialogue-consequences.md for the full method and the module-by-module hit list).
///
/// Method: `CSWVirtualMachineCommands::ExecuteCommandAddAvailableNPCByTemplate` is at
/// `0x0052DCD0` (already known from the prior round that built <see cref="Odyssey.AddAvailableNpcByTemplate"/>).
/// `tools/xrefs.py 0x52DCD0` finds its ONE address-literal occurrence in the binary at
/// `0x0054CBC4` — the routine-table population site (`mov dword [eax+0x0AE4], 0x0052DCD0`, the
/// same "store the handler pointer at a fixed offset into the 2048-entry dispatch table" shape
/// `docs/research/dialogue-consequences.md` §1.1 already used for seven OTHER routines, each one
/// cross-checked there by the same `offset / 4 = routine number` arithmetic). `0x0AE4 / 4 = 697`,
/// so `AddAvailableNPCByTemplate` is NWScript routine **697** — traced this round for the first
/// time (the doc's §4.2 "not yet traced" note is now closed).
///
/// A scan of every `.ncs` inside every `modules/*.rim` for the 6-byte ACTION pattern
/// `05 00 02 B9 <argc>` (0x02B9 = 697, format VERIFIED against `min-one-hitpoint.md`'s own
/// `05 00 <u16 routine> <u8 argc>`), reading the `CONST Int` immediately preceding the call
/// (`04 03 <i32>`, the same literal-argument shape that document already used for
/// `SetMinOneHP`), found **46 real hits across 32 distinct scripts in 24 modules** — every
/// single one carrying, in the SAME call, both the `nNPC` integer AND a `CONST String` for
/// `sTemplate` that names one of the nine resrefs below directly (e.g.
/// `tar_m05aa_s.rim:k_ptar_addzaal` pushes `CONSTI 8` immediately followed by the string
/// `"p_zaalbar"` before the routine-697 ACTION). Every companion's hits agree with each other
/// (no script contradicts another for the same resref) and slot 2 = `p_carth` reproduces the
/// project's own independently live-confirmed value
/// (`docs/research/party-reconciliation.md` round 75, `console 'recruit 2 carth'`). This is
/// materially stronger evidence than the prior round's "matches the standard nwscript.nss
/// declaration order" — it is the actual shipped recruit scripts naming both fields together,
/// not an inferred ordering.
///
/// | Slot | Companion | Resref | Hit count | Earliest non-cutscene / non-endgame-rejoin script |
/// | --- | --- | --- | --- | --- |
/// | 0 | Bastila | `p_bastilla` | 15 | `tar_m03aa_s.rim:k_ptar_bastpart` (Taris) |
/// | 1 | Canderous | `p_cand` | 5 (4 spell it `p_cand`; ONE, `tar_m02aa_s.rim:k_ptar_davikest`, spells it `p_canderous` — a resref that does NOT exist anywhere in `chitin.key`/the BIFs, so that specific call would fail if ever reached; treated as leftover/dead script content, not evidence against `p_cand`) | `tar_m08aa_s.rim:k_ptar_candadd` (Taris) |
/// | 2 | Carth | `p_carth` | 9 | Endar Spire recruit is scripted, not an ACTION 697 call reachable this way; Taris re-add: `tar_m02aa_s.rim:k_ptar_addcarth`. Slot LIVE-CONFIRMED (party-reconciliation.md round 75). |
/// | 3 | HK-47 | `p_hk47` | 2 | `tat_m17ac_s.rim:k_ptat_hk47add` (Tatooine) |
/// | 4 | Jolee | `p_jolee` | 5 | `kas_m24aa_s.rim:k_pkas_joleejoin` (Kashyyyk) |
/// | 5 | Juhani | `p_juhani` | 3 | `danm13_s.rim:k_pdan_vandar02` (Dantooine) |
/// | 6 | Mission | `p_mission` | 2 | `tar_m04aa_s.rim:k_ptar_addmissio` (Taris) |
/// | 7 | T3-M4 | `p_t3m4` | 2 | `tar_m02ab_s.rim:k_ptar_addt3m4` (Taris) |
/// | 8 | Zaalbar | `p_zaalbar` | 1 | `tar_m05aa_s.rim:k_ptar_addzaal` (Taris) |
///
/// All nine resrefs were separately re-confirmed present in `templates.bif` this round via
/// `tools/kotorfiles.py`, with each one's `Tag`/`FirstName` GFF fields read back and each
/// `FirstName` `StrRef` resolved through `dialog.tlk` — e.g. `p_hk47`'s `Tag` is literally
/// `HK47` and its `FirstName` strref resolves to `"HK-47"` — closing the separate risk that a
/// resref name alone (`p_cand` for "Canderous", `p_t3m4` for "T3-M4") could be misleading.
///
/// Still NOT live-confirmed in a running two-instance test for slots other than 2 (Carth) — see
/// docs/research/dialogue-consequences.md's live-test recipe for the remaining eight.
/// </summary>
internal static class PartyRosterTemplateTable
{
    /// <summary>Order is the party-roster slot index (0-8), matching the NPC_* constants'
    /// standard nwscript.nss declaration order and this round's independent disassembly
    /// verification (see the class doc comment's table).</summary>
    private static readonly string[] Templates =
    {
        "p_bastilla", // slot 0 - Bastila
        "p_cand",     // slot 1 - Canderous
        "p_carth",    // slot 2 - Carth (live-confirmed, party-reconciliation.md round 75)
        "p_hk47",     // slot 3 - HK-47
        "p_jolee",    // slot 4 - Jolee
        "p_juhani",   // slot 5 - Juhani
        "p_mission",  // slot 6 - Mission
        "p_t3m4",     // slot 7 - T3-M4
        "p_zaalbar",  // slot 8 - Zaalbar
    };

    /// <summary>Number of party-roster slots K1's engine defines. Matches
    /// <c>Odyssey.PartyRosterSlots</c>/<c>SharedBlock.PartyRosterSlots</c> — kept as a separate
    /// constant here (rather than a cross-project reference) so this table has no dependency on
    /// the native-facing classes, only on itself.</summary>
    public const int SlotCount = 9;

    /// <summary>The story blueprint resref for one party-roster slot, or <c>false</c> if the
    /// slot is out of K1's fixed 0-8 range.</summary>
    public static bool TryGetTemplate(int slot, out string resref)
    {
        if (slot < 0 || slot >= Templates.Length)
        {
            resref = string.Empty;
            return false;
        }

        resref = Templates[slot];
        return true;
    }
}
