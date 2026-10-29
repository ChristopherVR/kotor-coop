# KMRP connection compatibility

## 2026-10-30 — false executable mismatch with matching runtime patches

**Report:** two players could connect on clean installs over Hamachi, then received the
executable-version refusal after both installed KMRP 1.5.1 for Windows. No support bundle
or live image dumps accompanied the report.

**VERIFIED by source inspection:** co-op hashes the loaded executable's complete `.text`
before installing its own hooks. This solves the Steam encrypted-file mismatch but does
not exclude another mod's already-installed hooks. KMRP's pinned
[1.5.1 applier](https://github.com/RayesDiyab/KMRP-KOTOR-Modern-Restoration-Patch/blob/v1.5.1/src/controller-native/K1KpmApplier.cpp)
allocates its code with `VirtualAlloc(nullptr, ...)` and adds the resulting address delta
to operands in game-code patch runs. Its
[runtime documentation](https://github.com/RayesDiyab/KMRP-KOTOR-Modern-Restoration-Patch/blob/v1.5.1/src/kpm-runtime/README.md)
also describes deferred patching on Steam. Equal releases therefore need not produce
equal bytes at co-op's startup sampling point.

**INFERRED:** runtime allocation or patch timing explains the reported refusal. Without
the two players' logs, this is a demonstrated failure mechanism, not a confirmed diagnosis
of their particular machines. It is independent of Hamachi's packet routing.

### Change

The original live image digest and strict default remain. A separate optional SHA-256
identity covers a supported KMRP install's executable file, `patch_config.toml`,
`KotorPatcher.dll`, Bink proxy and original, root DLL/ASI files, root mod INIs, the complete
`patches` and `configs` trees, and any `dinput8.chain` plus its local DLL entries.
Names are relative and ordered; full file contents are hashed without a timestamp cache.
`swkotor.ini`, `coop.ini`, root logs and absolute installation paths are excluded. Screen
resolution can differ while patch inputs match. Patch-option INIs must match exactly.

The accepted config shape is the relative `patches/<id>.dll` layout emitted by
[KMRP 1.5.1's installer](https://github.com/RayesDiyab/KMRP-KOTOR-Modern-Restoration-Patch/blob/v1.5.1/src/patcher/KpmEdition.cs).
There must be a `kmrp` entry, all referenced modules must exist, and KMRP/controller
option files must exist when applicable. Missing/unreadable inputs, linked paths, external
module paths and unrecognized layouts supply no fallback. Inputs stay open without
write/delete sharing during hashing; the directory census and parsed config are checked
again before publishing. This is an install-time check; install or update mods only with
the game closed, then restart both games.

If both live images are known and differ, matching nonempty KMRP identities admit the
connection with `W_KMRP`. The other content compatibility rules still apply. Absent,
malformed or differing identities retain `E_EXE`, unless the host explicitly chooses the
existing `exe_mismatch = allow` setting. A bare mod name/version is never sufficient.
The error message now mentions runtime patches and settings as well as KOTOR 1.03.

Protocol 87 / content scheme 3 carries this separate field in Hello and Welcome. Both
players must update. Raw image hashes remain in diagnostics alongside the KMRP identity.
The file identity deliberately does not normalize different storefront executables: the
existing live-image route still handles ordinary Steam/GOG interoperability; this fallback
requires identical executable file bytes too.

### Verification and limits

**VERIFIED by automated tests:** same relative install contents in different folders match
despite different timestamps, resolutions and simulated runtime detour addresses; same-size
edits with restored timestamps change identity; missing/locked inputs and unsupported
paths disable the fallback; changed runtime modules still refuse. Hello/Welcome preserve
the identity and reject truncation. Real UDP loopback tests cover admission with matching
files and refusal with a changed patch, with `exe_mismatch` off on both peers. Appearance
mismatches still disable their channel.

Validation on this checkout: `dotnet build KotorCoop.slnx --no-restore -warnaserror`
passed with no warnings. The CI-filtered suite (`Category!=Soak&Category!=LocalOnly`)
passed 6,406 tests and exposed one new test-fixture error: an uninitialized behaviour
digest is normalized by the existing decoder. After using `BehaviourDigest.Default` in
that fixture and completing the input-audit tests, the affected suites rebuilt with
warnings as errors and passed all 197 tests. The entire broad suite was not rerun after
those final changes. `git diff --check` passed.

**NEEDS-LIVE:** install the built agent on two licensed game copies with matching KMRP
1.5.1 options, restart both games, join, create/load characters, move, fight, talk and cross
an area boundary. Repeat with different screen resolutions. This change addresses admission;
matching installed files do not prove that both runtimes applied successfully or that their
hooks coexist with co-op. KMRP gameplay, its menu layout and its controller support are not
certified by the synthetic or socket tests. No engine memory writes or plugin hooks changed.
