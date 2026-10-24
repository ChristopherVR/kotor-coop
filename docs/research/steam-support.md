# Steam support — the Steam build, and native Steam invites (2026-08-29)

## Current verification — 2026-10-24

This update qualifies the older reports below; historical observations are not a fresh
two-account acceptance test. The focused Steam and executable-digest suite passed **125/125**
tests after this review. Read-only checks against the installed executables identify the
Steam copy as `SteamStub` and the GOG copy as `DrmFree`. With Steam running, the current native
loader was exercised without `steam_api64.dll`: it returned false and logged the expected
optional-feature message. No game files or running Steam sessions were changed.

The review fixed these problems:

- Do not print rich-presence connect strings in publish, startup or status messages: their
  base64 password token is reversible and must not leak into support logs/screenshots.
- Reject control characters, invalid bracketed IPv6, nonnumeric ports, noncanonical token
  alphabets and invalid UTF-8 passwords. Limit the encoded string to 255 UTF-8 bytes, reserving
  the final byte in Steam's 256-byte callback buffer for the NUL terminator.
- Preserve an existing `steam_appid.txt` naming a different application and leave invites off
  instead of overwriting the file. File creation also refuses a concurrent overwrite.
- Re-bracket IPv6 addresses when handing a live callback to the normal connection command.
- Respect a failed `SetRichPresence` result instead of announcing success; bound callback
  reads to the SDK's 256-byte field; shut down an initialized API even after a later failure.

Neither installed game supplies the optional 64-bit Steam redistributable. No library was
borrowed from another game for this check. New native success-path testing, a real friend
receiving/accepting an invitation, and cross-network joining remain **NEEDS-LIVE**. The earlier
self-invite and direct loopback tests do not establish those behaviors. A failure to reach
one's public endpoint is also not by itself proof of NAT hairpinning as the sole cause.
The later same-PC cold-start forwarding and world-entry check is recorded in section 20.
Supplying an argument locally still does not establish Steam friend delivery.

API contract reference: [Valve's ISteamFriends documentation](https://partner.steamgames.com/doc/api/ISteamFriends)
for `SetRichPresence`, `InviteUserToGame` and `GameRichPresenceJoinRequested_t` (checked 2026-10-24).

## Historical investigation

Two questions asked together: *does this mod work on the Steam copy of KOTOR at all*, and
*can two players start a session from Steam's own friends list ("Join Game") instead of
typing an IP*. They have very different answers. The first is settled — measured, from
disk, on this machine. The second is a design with one hard external dependency that this
wave deliberately does not ship.

Tag key: **VERIFIED** = read from the binary here, from the source in this worktree, or
covered by a passing test · **INFERRED** = deduced, not yet run · **NEEDS-LIVE** = must be
confirmed on the running game.

## Headline

**The Steam build is the same binary we already reverse-engineered.** Not "the same
version" — the same *link*, byte for byte, under a DRM wrapper. Steam ships retail 1.0.3
wrapped in Valve's own SteamStub v3, which encrypts only the `.text` section on disk and
decrypts it in place at load. Every other section — `.rdata`, `.data`, `.rsrc`, and the
wrapper's own `.bind` — is byte-identical to the DRM-free exe this project's entire address
database was built against. Same `ImageBase`, same section table, same PE timestamp, same
import table. **Every hardcoded address in `Odyssey*.cs` is the same VA on the Steam build
at runtime**, and no per-build address table is needed (§7 says what to build instead of
one, and why). `PackageInstaller`'s current "Steam (not supported: … the binary differs)"
label is wrong on its second clause and should be corrected.

> **Update, 2026-08-29 (§18).** Everything below was written from disk, before the rig had run
> a Steam instance. It has now been run: the Steam build hooks cleanly with the overlay on, §8's
> fix is shipped (the digest hashes the running image), and a Steam client joined a DRM-free
> host in one live session. **§18 supersedes the NEEDS-LIVE/INFERRED tags in §§3, 4, 6, 8 and
> 17.** The design reasoning below is left exactly as it was written; §18 says what happened.

**What blocked Steam — our own compatibility handshake, not the engine. Fixed in §8/§18.**
`ContentCompatibility` hard-refuses a session whose two `ExeDigest`s differ
(`RefusalReason.Executable`), and `InstallDigest` hashes the file on disk — so a wrapped
Steam host and an unwrapped host hash differently and refuse each other, even though the
code they are about to hook is identical. Steam↔Steam is fine. **Steam↔DRM-free is refused,
and that is a bug in the digest, not a real incompatibility** (§8).

**Native Steam invites are feasible but need `steam_api64.dll`, which we must not ship.**
The Steamworks flat API is the only way to publish a rich-presence `connect` string and
receive `GameRichPresenceJoinRequested`, and it lives in a native DLL that Valve licenses
to Steamworks partners, not to mod authors. The Steam *client* does not ship one (VERIFIED:
searched; only individual games carry it). So phase 1 ships everything that does not need
it — the connect-string codec, the config key, the CLI verb, the join wiring, and a
lazily-bound facade that logs one line and does nothing when the DLL is absent — and phase 2
adds the binding behind an explicit, user-supplied file. §§9–16.

---

# PART 1 — is the Steam build compatible?

## 1. The two binaries, measured

Both files present on this machine (VERIFIED):

| | path | size | SHA-256 |
| --- | --- | --- | --- |
| DRM-free (the RE target) | `C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe` | 4,395,008 | `D3432B32…173E381A` |
| Steam | `C:\Program Files (x86)\Steam\steamapps\common\swkotor\swkotor.exe` | 4,395,008 | `34E6D971…39F34C88` |
| the wrapped original, kept beside the unwrapped one | `C:\GOG Games\…\swkotor.rexa` | 4,395,008 | `34E6D971…39F34C88` |

The third row is the whole proof in one line: **the DRM-free install directory still
contains the wrapped binary it was made from, and that file is byte-identical to the one
Steam ships today.** The "GOG" folder is a Steam install whose `swkotor.exe` was unwrapped
in place, the original renamed aside. It also still carries Steam's own
`32370_install.vdf`. So the exe this project has spent months disassembling is, provably,
*the Steam exe with its DRM removed* — not a different release that merely happens to agree.

PE headers agree on everything that matters (VERIFIED):

```
                     unwrapped            Steam
machine              0x014C               0x014C
timestamp            0x402BC2D9           0x402BC2D9    (2004-02-12 18:15:53 UTC)
ImageBase            0x00400000           0x00400000
AddressOfEntryPoint  0x002FB38D           0x0046D2ED    <-- the ONLY header difference
.text    vaddr 0x00001000 vsize 0x0033B1D0 rawptr 0x00001000 rawsize 0x0033C000   (both)
.rdata   vaddr 0x0033D000 vsize 0x0004FBFE rawptr 0x0033D000 rawsize 0x00050000   (both)
.data    vaddr 0x0038D000 vsize 0x000A8498 rawptr 0x0038D000 rawsize 0x00017000   (both)
.rsrc    vaddr 0x00436000 vsize 0x00036BD8 rawptr 0x003A4000 rawsize 0x00037000   (both)
.bind    vaddr 0x0046D000 vsize 0x00056000 rawptr 0x003DB000 rawsize 0x00056000   (both)
```

There is no `.reloc`: the image is fixed-base at `0x00400000` and cannot be rebased, so an
absolute address is an absolute address on both.

Import tables are identical, entry for entry, including the same `firstThunk` RVAs
(VERIFIED) — `OPENGL32`, `KERNEL32`, `USER32`, `GDI32`, `ole32`, `binkw32`, `mss32`,
**`DINPUT8`** (`firstThunk` RVA `0x0033D000` on both), `GLU32`, `VERSION`, `IMM32`. Neither
build imports `steam_api.dll`; the game has no Steamworks integration of its own at all
(VERIFIED — no `steam_api*.dll` anywhere in the Steam KOTOR directory).

## 2. The diff is exactly one section

`cmp -l` reports 3,378,359 differing bytes — 77% of the file — which looks alarming until
you plot where they are. Bucketed over 64 KiB (VERIFIED):

```
0x00000000   61359 / 65536   93.6%     <- 4 header bytes (AddressOfEntryPoint) + .text from 0x1000
0x00010000 … 0x00320000  ~65280 / 65536  ~99.6%   <- .text, every bucket
0x00330000   53037 / 65536   80.9%     <- .text ends at raw 0x33D000
0x00340000 onwards            0 / 65536      0%   <- .rdata, .data, .rsrc, .bind: IDENTICAL
```

The differing range is precisely `.text`'s raw extent `[0x1000, 0x33D000)`, plus the
four-byte entry-point field. Nothing else in the file differs — not one byte of `.rdata`
(where the vtables and string literals every piece of our RE keyed off live), not one byte
of `.data`, not one byte of `.rsrc`.

Shannon entropy over 256 KiB samples says why (VERIFIED):

```
              unwrapped   Steam
.text @0x001000   6.3946   7.9993      <- 8.0 bits/byte is ciphertext, not code
.text @0x100000   6.2999   7.9994
.text @0x200000   6.4402   7.9993
.rdata @0x33D000  5.2536   5.2536      <- identical file, identical entropy
.bind  @0x3DB000  7.9896   7.9896
```

`.text` in the Steam file is encrypted. `.text` in the unwrapped file is ordinary MSVC x86
(22,150 `0xCC` alignment-padding bytes, versus 13,216 statistically-random ones in the
ciphertext).

## 3. The wrapper is SteamStub v3, not SecuROM

Worth pinning down, because the two have very different runtime behaviour. The evidence
(VERIFIED):

* The wrapper section is named **`.bind`** and the Steam entry point lives inside it
  (RVA `0x0046D2ED`, i.e. `.bind` + `0x2ED`). SecuROM 4/5 uses `.cms_t`/`.cms_d`; neither
  section exists here.
* Immediately before the entry point sit the bytes `EB 04 DE C0 DE C0` — a `jmp +4` over
  the little-endian dword `0xC0DEC0DE`, landing exactly on `AddressOfEntryPoint`. That
  marker-then-OEP layout is Valve's SteamStub signature, and `0xC0DEC0DE` occurs exactly
  once in the whole 4.4 MB image.
* The stub entry itself is `53 51 52 56 57 55 8B EC 81 EC 00 10 00 00 C7 85 …`
  (`push ebx/ecx/edx/esi/edi/ebp; mov ebp,esp; sub esp,0x1000; mov [ebp-0x88], 0x0086F204`)
  — the SteamStub variant-3 x86 prologue.
* The `HKEY_LOCAL_MACHINE\SOFTWARE\BioWare\SW\SecuROM` block in Steam's
  `32370_install.vdf` (with its "Please insert the … Play disc" strings) is a **retail
  install-script leftover**: it writes registry strings, and there is no SecuROM code in
  the image to read them. Do not be misled by it — it is the one piece of evidence that
  points the wrong way.

Why the distinction matters: SecuROM does continuous, in-play integrity verification and
anti-debug work, and would be a genuine threat to inline hooks. SteamStub v3 is a
load-time unpacker — it decrypts `.text`, restores imports, checks that Steam is running
and the app is owned, and jumps to the OEP. It does not stay resident re-checksumming the
code section. **INFERRED**, from the wrapper's known shape rather than from instrumenting
it; the practical confirmation is §6's live checklist.

## 4. Therefore: the addresses are the same, and this is provable rather than hopeful

The three probes asked for, at their file offsets (`.text` has `rawptr == vaddr`, so
file offset = RVA = VA − `0x400000`) — VERIFIED:

| symbol | VA | unwrapped bytes | Steam bytes |
| --- | --- | --- | --- |
| `CSWSObject::StartDialog` | `0x004CF490` | `6A FF 68 9E 9D 71 00 64 A1 …` | `27 D5 D4 CB EE 4B CA DE 2B …` |
| `CItemRepository::InsertItem` | `0x0055D2C0` | `F6 41 08 01 75 60 8B 44 24 08 …` | `C2 70 29 0B CF D3 05 C0 CE …` |
| `CClientExoAppInternal::DoQuickSave` | `0x005F4B50` | `6A FF 68 50 5C 72 00 64 A1 …` | `34 E5 47 94 1E 20 B5 4A 19 …` |

The unwrapped column is exactly what `docs/research/engine-api.md` describes: two of the
three are the textbook MSVC SEH prologue `push -1; push <handler>; mov eax, fs:[0]`. The
Steam column is ciphertext, as every byte of its `.text` is **on disk**.

That is the important qualifier. The addresses match **at runtime**, because:

1. SteamStub decrypts `.text` in place, at the same RVAs, in an image that cannot be
   rebased; and
2. the unwrapped file *is* that decryption, written back — which is what "the wrapped
   original is still sitting beside it as `swkotor.rexa`" proves. An unwrapper produces its
   output by performing the stub's own decryption and restoring the OEP; the fact that the
   two files differ in `.text` **and nothing else** is the signature of exactly that
   operation, and rules out any additional patching having been done to the copy we
   reverse-engineered.

So there is no per-build address divergence to abstract over. A disk-based signature scan
of the Steam exe is impossible (there is nothing to match — it is ciphertext), and
unnecessary.

## 5. `dinput8.dll` loads identically

`DINPUT8.dll` is a *static* import of both files at the same `firstThunk` (VERIFIED, §1), so
the Windows loader maps our proxy from the application directory before the entry point
runs, on both builds. `dinput8.dll` is not a KnownDLL, so the game directory wins the search
order. Nothing about the wrapper changes this — the import descriptors live in `.rdata`,
which is byte-identical.

Ordering against the stub is favourable, and by accident rather than design (VERIFIED from
`src/KotorCoop.Plugin/Exports.cs:208`): **we do not install hooks from `DllMain`.** The
plugin starts from the `DirectInput8Create` export, which the game calls during its own
startup — that is, long after the stub has finished decrypting `.text` and jumped to the
OEP. If we hooked at load time we would be patching ciphertext and the stub would overwrite
us; we do not, so the sequence is:

```
loader maps swkotor.exe + dinput8.dll (ours)      -> our DllMain, which installs nothing
SteamStub entry 0x0086D2ED                        -> decrypts .text in place, restores IAT,
                                                     ownership check, jmp OEP 0x006FB38D
game startup -> DirectInput8Create (ours)         -> PluginHost.EnsureStarted, hooks installed
```

The same ordering is why a startup-time integrity check by the stub cannot see our patches:
it runs before there are any.

The chain loader (`src/KotorCoop.Loader`, `dinput8.chain`) is unaffected — it is a file-name
convention among mods in the game directory and has nothing to do with the exe's DRM.

## 6. Steam's overlay is the one real conflict, and the code already knows

`GameOverlayRenderer.dll` (x86, 1,347,224 bytes) ships with the client and is injected into
every Steam-launched game (VERIFIED it exists; injection is standard behaviour). KOTOR is an
OpenGL title that presents through **`GDI32!SwapBuffers`**, and `FrameHook` gets its
once-per-frame callback by overwriting the single IAT slot at `0x0073D00C` that holds it
(`src/KotorCoop.Plugin/FrameHook.cs`).

`FrameHook.Install` already validates the slot before touching it and names the culprit:

```
IAT slot 0x73D00C holds 0x... but gdi32!SwapBuffers is 0x...; something else already hooked it. Refusing.
```

with the comment *"If some other layer (the Steam overlay, for instance) has already wrapped
it … refuse rather than corrupt the import."* So the failure mode is a clean refusal with a
diagnostic, not corruption. Two outcomes are possible and only a live run separates them
(**NEEDS-LIVE**):

* the overlay detours `gdi32!SwapBuffers` **inline** (patches the function prologue in
  gdi32) — then `GetProcAddress` returns the same address the IAT still holds, our check
  passes, and both coexist with the overlay outermost;
* the overlay rewrites the game's **IAT slot** — then we refuse and the mod is inert, and
  the fix is either to install before the overlay does or to fall back to detouring
  `gdi32!SwapBuffers` ourselves.

Mitigation if it bites: telling players to disable the Steam overlay for KOTOR
(Properties → General → uncheck "Enable the Steam Overlay while in-game") is a one-click
fix and should be the documented answer before any code change.

## 7. What to build instead of a per-build address table

The abstraction the brief asked for — "a per-build address table keyed by exe hash, with
signature scanning as the fallback" — is the right shape for a game with several different
links. It is the wrong shape here: there is exactly one link, and the only variation is
whether it is wrapped. Building a table would add a lookup, a failure mode, and a second
source of truth for the ~9,700 addresses in `tools/kotor1_0_3.db`, in exchange for nothing.

What is worth building instead is **build *recognition*** — a cheap, pure, offline check of
which of the two forms an install is in, so the mod can say something useful rather than
guess. Everything it needs is in the PE header:

```
wrapped (Steam)   : AddressOfEntryPoint falls inside a section named .bind, and the four
                    bytes at (entry - 4) are DE C0 DE C0
unwrapped         : AddressOfEntryPoint falls inside .text
either            : ImageBase 0x00400000, five sections, TimeDateStamp 0x402BC2D9
```

That is what `SteamBuild` (implemented this wave, `src/KotorCoop.Memory/SteamBuild.cs`,
unit-tested) does. It is used for diagnostics today and is the input to §8's fix. It reads a
file; it never launches anything.

If a second genuinely different link ever turns up (a real GOG-store release with a
different layout, say), the escalation path is: keep the current addresses as the default
set, add `SteamBuild`-style recognition for the new one, and only then introduce a table.
Not before.

## 8. THE ACTUAL BLOCKER: our own exe digest refuses cross-store play — **FIXED, option 1**

> **Shipped 2026-08-29.** Option 1 below is what was built, with one change the design did not
> anticipate: the hash is taken by the PLUGIN, inside the game, *before it installs a single
> hook*, and published at `SharedBlock.OffsetExeImageDigest` for the agent to carry — not by
> the agent reading the process afterwards. The reason is that our own inline detours rewrite
> `.text`, and which ones get installed depends on coop.ini, so an agent-side read taken after
> attach would differ between two peers whose CONFIG differs and would refuse a session that
> works. `ExeImageDigestRule` (KotorCoop.Protocol, compiled into the plugin by source) also
> folds in the PE timestamp, `ImageBase` and every section header, and deliberately EXCLUDES
> `AddressOfEntryPoint` — the one header field the wrapper changes. The file hash survives as
> a log label. Live results: §18.

`InstallDigest.TryHashExecutable` FNV-1a-hashes **the file on disk**, and
`ContentCompatibility` (`src/KotorCoop.Protocol/ContentCompatibility.cs:202`) treats a
mismatch as fatal:

```csharp
// EXE — refuse. Every engine address the mod uses is absolute and per-binary; a
// different exe means every write could land somewhere unrelated.
if (BothKnow(local, remote, ContentDigest.Field.Exe) && local.ExeDigest != remote.ExeDigest)
{
    return Refuse(RefusalReason.Executable);
}
```

There is no override for it — not `--require-identical-save`, not `auto_adopt_world`,
nothing. The reasoning in that comment is exactly right *in general* and exactly wrong *for
this pair*: the wrapped and unwrapped files hash differently and hook identically. So today
(VERIFIED by reading the code, **NEEDS-LIVE** to see the refusal in a real session):

| host | client | verdict |
| --- | --- | --- |
| Steam | Steam | works — both wrapped, same hash |
| DRM-free | DRM-free | works — today's rig |
| Steam | DRM-free | **refused, `RefusalReason.Executable`** |

**The fix, for the wave that next bumps the protocol version.** Do not weaken the check;
make it describe the right thing. Two options, in preference order:

1. **Hash the live image, not the file.** The agent already has `ProcessMemory` open on the
   game and attaches long after the stub has run, so `.text` is plaintext by then. Hashing
   `[ImageBase+0x1000, +0x33D000)` out of the process makes wrapped and unwrapped agree
   *because they genuinely are the same bytes*, and makes the check strictly stronger — it
   would also catch a runtime patcher that a file hash misses. Costs one 3.4 MB read at
   startup.
2. **Add a second, wrapper-invariant field.** Keep `ExeDigest` as the file hash (demoted to
   a note), add `ExeImageDigest` over `.rdata + .data + .rsrc + the section table + the PE
   timestamp` — the bytes proven identical in §2 — and refuse on *that*. Cheaper (no process
   read) but weaker: it stops noticing `.text` mods entirely.

Both change what `ContentDigest` means on the wire and so must land with a
`ProtocolConstants.Version` bump, which is why this wave designs it rather than shipping it.
Until then, cross-store sessions are refused and the honest instruction is "both players on
the same store".

---

# PART 2 — native Steam invites

## 9. Which process talks to Steam

The agent, not the plugin. Not a close call:

* The plugin is x86 NativeAOT inside `swkotor.exe`; the Steamworks redistributable most
  readily available is x64, and putting a Steam client connection inside the game process
  buys nothing.
* The agent is an ordinary x64 .NET process, already owns the session lifecycle
  (`SessionManager`), already knows the host address and port, already has a public
  address from STUN, and already exposes `CoopCommandDispatcher.OnConnectRequest(string)`
  as *the* "something outside asked us to join this address" entry point — the same one the
  in-game Co-op button, the browser panel and the `~` console all funnel through.
* An incoming Steam join request therefore lands as one call into machinery that is already
  written and already tested.

## 10. Binding choice: none of them

| option | licence | fit |
| --- | --- | --- |
| Steamworks.NET | MIT wrapper; **needs `steam_api64.dll`** | thin, 1:1, well-maintained, AOT-tolerable. Adds `Steamworks.NET.dll` to the publish output. |
| Facepunch.Steamworks | MIT wrapper; **needs `steam_api64.dll`** | nicer API, generated interop, weaker trimming story, quieter maintenance. Adds `Facepunch.Steamworks.Win64.dll`. |
| hand-rolled P/Invoke | none | ~8 exports. No new files in the publish output. |

The deciding constraint is not the licence of the wrapper — both are MIT — it is
`tools/package.ps1`:

```powershell
$stray = Get-ChildItem $agentPublishDir -File | Where-Object { $_.Name -notlike 'KotorCoop.*' }
if ($stray) { throw "agent publish contains non-KotorCoop.* files the installer cannot track: ..." }
```

The packager *throws* on any file in the agent publish that is not named `KotorCoop.*`,
because the uninstaller's rule for what it owns is "everything `KotorCoop.*`". A NuGet
binding breaks the build; so would a bundled `steam_api64.dll`. **So: hand-rolled.** The
flat API is a stable C ABI and we need very little of it:

```
SteamAPI_Init / SteamAPI_Shutdown
SteamAPI_ManualDispatch_Init / _RunFrame / _GetNextCallback / _FreeLastCallback
SteamAPI_SteamFriends_v017            (interface accessor)
SteamAPI_ISteamFriends_SetRichPresence
SteamAPI_ISteamFriends_InviteUserToGame
SteamAPI_SteamUser_v023 + _GetSteamID
```

`SteamAPI_ManualDispatch_*` exists precisely so non-C++ bindings can pump callbacks without
the C++ `CCallback` machinery, which is the part that makes hand-rolling tractable. The one
callback we need is `GameRichPresenceJoinRequested_t`, id **337**
(`k_iSteamFriendsCallbacks` 300 + 37), whose payload is `{ CSteamID m_steamIDFriend; char
m_rgchConnect[256]; }`.

## 11. The `steam_api64.dll` problem, stated plainly

**24 October 2026 correction:** the historical assessment below was too categorical
about who may redistribute the SDK and incorrectly suggested that ownership of another
game establishes reuse rights to its DLL. Valve's current
[SDK Access Agreement](https://partner.steamgames.com/documentation/sdk_access_agreement/)
section 1.1 grants licensees specified redistributable rights with their object-code
software; it does not by itself settle this project's licensing or AppID/service access.
The public beta excludes the optional DLL and does not recommend copying it from other
games. Native friend invitations remain unverified. Historical reasoning is retained
below as a record, not current installation guidance.

Every option above needs that file, and we cannot supply it:

* Valve's Steamworks SDK licence permits redistribution by **Steamworks partners shipping
  on Steam**. A mod distributed as a GitHub zip is not that. Bundling it is not something
  this project should do on a "probably fine" reading.
* The Steam **client** does not contain one (VERIFIED: a recursive search of
  `C:\Program Files (x86)\Steam` finds `steam_api64.dll` only inside *individual games'*
  directories — twelve of them on this machine — and never in the client's own `bin`,
  `public`, or `package` folders). So we cannot borrow it from a guaranteed location.
* `steamclient64.dll` **is** shipped by the client (26 MB, present) and is what
  `steam_api64.dll` itself loads and forwards to. Driving it directly (`CreateInterface`,
  pipe/user handshake) needs no redistribution at all — but it is an undocumented internal
  interface with a version-suffixed name that Valve changes, and a broken co-op mod after a
  client update is a bad trade for saving the user one file copy. Rejected for now; recorded
  because it is the only zero-file option.

**The phase-2 answer** is therefore: the user supplies the file, once, deliberately. Either
`KotorCoop.Agent.exe install-steam --from <path-to-steam_api64.dll>` (copied from a
Steamworks SDK download or from any Steam game they own), or simply "drop `steam_api64.dll`
next to `KotorCoop.Agent.exe`". `NativeLibrary.TryLoad` finds it or it does not, and the
whole feature stays inert either way — which is what §12's fail-soft rule is for.

## 12. `steam_appid.txt` = 32370, and what it costs

`SteamAPI_Init` needs to know which app it is. Outside a Steam-launched process that comes
from a `steam_appid.txt` beside the executable. Writing `32370` there makes the agent
present *as KOTOR*, which is what makes "Join Game" appear on a friend's entry.

Consequences, all of which have to be accepted deliberately:

* **The user must own KOTOR on Steam.** `SteamAPI_Init` fails for a non-owner. A GOG-only
  player gets `false`, one log line, and the LAN/direct-IP path exactly as it works today.
  This is the whole reason the feature is optional and fail-soft rather than a dependency.
* **Steam will show the user as playing KOTOR** while the agent runs. Since the agent only
  runs alongside the game, that is honest, but it is a visible side effect and belongs in
  the README.
* `steam_appid.txt` beside `KotorCoop.Agent.exe` means beside `swkotor.exe`, since the agent
  is installed into the game root. That is also where the *game* would look if it were a
  Steamworks app — it is not (§1), so there is no interference.
* It is a plain 5-byte text file with a fixed content, so unlike `steam_api64.dll` there is
  no licence question and the packager can ship it. It is inert without the DLL.

## 13. The connect string, and why it is a versioned rule

Steam's contract: a host calls
`SetRichPresence("connect", "<arbitrary string, ≤256 bytes>")`; a friend who picks "Join
Game" causes *their* client to launch the app with `+connect <that string>` on the command
line, or — if the app is already running — to deliver
`GameRichPresenceJoinRequested_t` carrying it. The string is opaque to Steam. It is,
however, **attacker-influenced input from the network's point of view**: it arrives from
another user's client and gets turned into a socket connection and a password. So it is
parsed by a pure, total, versioned rule with no exceptions thrown, in the house `*Rule`
style:

```
kc1/<host>:<port>[/<token>]
```

* `kc1/` — an explicit version prefix, so a future format can be recognised and rejected
  rather than half-parsed. Anything that does not start with a known prefix is rejected.
* `<host>` — IPv4/IPv6 literal or DNS name. Bracketed for IPv6, so the port colon is
  unambiguous.
* `<port>` — 1..65535, rejected otherwise.
* `<token>` — optional, URL-safe base64 of the session password. Present so a private
  session is joinable by invitation without the invitee being asked to type a password they
  were never told; **absent** when the host has no password. It is obfuscation, not
  encryption — Steam already restricts who can read a friend's rich presence, and the
  threat model here is "a password should not be sitting in a plaintext field", not "resist
  a determined attacker". Said plainly in the code so nobody mistakes it for security.

Total length is bounded well under Steam's 256, and the rule refuses anything longer rather
than emitting something Steam would silently truncate.

`SteamConnectStringRule` (`src/KotorCoop.Session/`) implements `Format`/`TryParse` and is
unit-tested against round-trips, IPv6, absent tokens, and every malformed shape.

## 14. Rich presence only. Not lobbies.

`ISteamMatchmaking` lobbies are the heavier alternative: a real Valve-hosted object with
members, metadata, chat, and a `LobbyGameCreated` handshake. They buy three things we
either already have or do not want:

* *a place to advertise a session before it starts* — we do not have a pre-session concept;
  a KotorCoop host is hosting the moment the agent says so;
* *matchmaking with strangers* — explicitly not a goal; the LAN browser plus a typed
  address is the existing story, and the whole point of this feature is inviting a *friend*;
* *member lists and lobby chat* — we have peers and an in-game chat overlay.

And they cost a whole second state machine that must be kept consistent with
`SessionManager`, plus every "the lobby and the session disagree" bug that implies. Rich
presence is one string, published on transition into hosting and cleared on transition out
— the same diff-and-repost shape `PublicAddressAnnouncer` already uses.

The one thing lobbies give that rich presence does not is a stable rendezvous identity
independent of IP, which matters only if the transport becomes SDR (§15). If that ever
happens, revisit; not before.

## 15. Steam Datagram Relay — the interesting one, deliberately not now

`docs/research/network-topology.md` §7 lists NAT traversal as the real missing piece for
internet play, and records that relay and hole-punching are out of scope. `ISteamNetworking
Messages` would solve it outright: connect by `CSteamID` instead of `IPEndPoint`, Valve's
backbone does the relaying, no port forwarding, no UPnP, no STUN, and the connect string
collapses to a SteamID.

It is genuinely attractive and it is genuinely a large change, because it replaces the
transport rather than sitting beside it: `UdpChannel` binds a socket and sends to an
`IPEndPoint`, and `CoopHost`/`CoopClient`, the world-transfer TCP path, the LAN browser and
the reconnect logic are all written against that. It also drags in the ownership
requirement (§12) for *both* players, which contradicts the "GOG users keep working"
constraint unless both transports are maintained forever.

Recorded as phase 3 with a precondition: only worth doing once phase 2 has shown that
enough of the player base has Steam for the second transport to pay for itself.

## 16. Recommended design, phased

**Phase 1 — everything that does not need the native DLL. (Implemented this wave.)**

* `SteamConnectStringRule` — pure, versioned, unit-tested codec (§13).
* `SteamBuild` — pure PE recognition of wrapped vs unwrapped (§7), unit-tested.
* `steam_invites` in `coop.ini`, ON by default and inert without Steam (§12's fail-soft).
* `KotorCoop.Steam` — a separate project holding the lazily-bound facade. Loads nothing at
  construction; `NativeLibrary.TryLoad("steam_api64.dll")` on first use, one log line on
  failure, every method a no-op afterwards. No NuGet, no bundled native file, output named
  `KotorCoop.Steam.dll` so the packager's `KotorCoop.*` rule holds.
* `--steam-join <connect-string>` on the agent, which decodes and dials through the existing
  join path. Testable with no Steam client at all — it is just a CLI verb.
* Host-side publish point wired to the same transition `PublicAddressAnnouncer` watches.

**Phase 2 — the binding.** The `steam_api64.dll` P/Invoke surface (§10) behind a
user-supplied file (§11); `SteamAPI_Init` with `steam_appid.txt`; `SetRichPresence("connect",
…)` on host; manual-dispatch pump for callback 337 into `OnConnectRequest`; the
`InviteUserToGame` path for inviting from our side. Ship the §8 digest fix in the same wave,
since without it a Steam↔GOG invite would connect and then be refused.

**Phase 3 — SDR**, only if phase 2 justifies it (§15).

---

## 17. What is verified, and what is not

**VERIFIED (from disk, on this machine, read-only):** both binaries' sizes, SHA-256s, PE
headers, section tables, import tables, entry points; that the diff is exactly `.text` plus
the entry-point field; the entropy of both `.text`s; the three probe addresses; the
SteamStub `0xC0DEC0DE` marker and its uniqueness; that the unwrapped install still holds the
wrapped original as `swkotor.rexa` with Steam's own hash; that no `steam_api*.dll` exists in
the Steam KOTOR directory or the Steam client; that `GameOverlayRenderer.dll` does; that
`FrameHook` already refuses a pre-hooked IAT slot; that the plugin starts from
`DirectInput8Create` and not `DllMain`; that `ContentCompatibility` hard-refuses an
`ExeDigest` mismatch; that `tools/package.ps1` throws on a non-`KotorCoop.*` publish file.

**INFERRED:** that SteamStub v3 performs no continuous integrity checking after the OEP
jump (§3); that the runtime `.text` of the Steam build equals the unwrapped file's `.text`
byte for byte (§4 — argued from the unwrap's provenance and the shape of the diff, not
observed in memory).

**NEEDS-LIVE, in priority order:**

1. Launch the Steam build with the plugin installed and read `kotorcoop-plugin.log`. The
   two things to look for are the `frame hook installed:` line (does the Steam overlay own
   the IAT slot? §6) and whether the game survives its first hooked frame (does the stub
   re-verify? §3).
2. A Steam↔Steam session, to confirm §4's address parity end to end rather than by argument.
3. A Steam↔DRM-free join, to see §8's `RefusalReason.Executable` refusal actually happen and
   confirm the fix is needed.
4. Whether a second Steam instance can be started at all — `--instance` relies on a renamed
   mutex and the DRM-free build; the stub's own ownership check and Steam's one-copy rule
   almost certainly forbid it (**INFERRED**). This affects the dev rig only, never two
   players on two machines.

None of these were run: this wave is research, design and non-live implementation, and the
rig was not used. **All four were run the next day — see §18, which supersedes the tags above.**

---

## 18. Live results (2026-08-29) — Steam runs, and a Steam client joined a GOG host

Everything §17 listed as NEEDS-LIVE was run, on this machine, with the rig lock held. The
short version: **the Steam build behaves exactly as §§1-6 argued, §8's fix is shipped, and a
cross-store session works.** The one inference §17 could not settle from disk — that the Steam
build's *runtime* `.text` equals the DRM-free file's `.text` — is now VERIFIED, in memory,
rather than argued from the unwrap's provenance.

### 18.1 The image digest: the two builds agree, the two files do not

Both installs, same demo save, plugin log line written before a single hook is installed:

```
GOG   [18:42:46.561] exe image digest C6962619 over .text 0x401000..0x73C1D0 (3387856 bytes), taken before any hook
Steam [18:39:05.064] exe image digest C6962619 over .text 0x401000..0x73C1D0 (3387856 bytes), taken before any hook
```

3,387,856 = `0x33B1D0`, `.text`'s virtual size from §1's section table, to the byte. And the
agents' own content-digest lines put the file hash and the image hash side by side:

```
GOG   content digest: exe image C6962619, 509 appearance rows, 2 override file(s), 49265 tlk entries [exe file 8B8EC44D (DrmFree)]
Steam content digest: exe image C6962619, 556 appearance rows, 2665 override file(s), 49349 tlk entries [exe file 220AC31D (SteamStub)]
```

**Different files (`8B8EC44D` vs `220AC31D`, SHA-256 `D3432B32…` vs `34E6D971…`), identical
running images (`C6962619`).** That is §8's whole claim, measured. §4's INFERRED line — "the
runtime `.text` of the Steam build equals the unwrapped file's `.text` byte for byte" — is now
**VERIFIED**: two independent processes each hashed 3.4 MB of their own decrypted `.text`,
plus the section table and the PE timestamp, and produced the same 32-bit value.

The digest is taken in `PluginHost.AllocateBlock`, *before* `InstallHooks`. That placement is
load-bearing rather than incidental: our own inline detours rewrite `.text`, and which ones
get installed depends on coop.ini, so a digest taken any later would differ between two peers
whose CONFIG differs and would refuse a session that works perfectly. One fixed point in
startup is what makes the two sides comparable at all.

### 18.2 The Steam build, hooked (NEEDS-LIVE #1)

Launched through the Steam client (`steam://rungameid/32370`), with `gameoverlayrenderer.dll`
loaded (**overlay ON** — confirmed in the process module list at `0x65EF0000`). Every hook
installed; **no byte-pattern refusal anywhere in the log**:

```
[18:39:05.054] forwarding to system C:\WINDOWS\system32\dinput8.dll (module 0x634C0000)
[18:39:05.057] === KotorCoop plugin loaded into pid 47028 ===
[18:39:05.060] enabled the developer console in swkotor.ini ([Game Options] EnableCheats=1)
[18:39:05.060] shared block at 0xFEE0000 (18432 bytes)
[18:39:05.064] exe image digest C6962619 over .text 0x401000..0x73C1D0 (3387856 bytes), taken before any hook
[18:39:05.077] frame hook installed: hooked IAT 0x73D00C (real SwapBuffers 0x779857F0)
[18:39:05.077] console interp hooked: FuncInterp hooked (trampoline 0xFEF0000)
[18:39:05.078] menu frame hook installed: MainLoop call site hooked (stub 0xFF00000)
[18:39:05.078] party faction guard: party faction guard installed (stub 0xFF10000)
[18:39:05.079] animations: animation hook at 0x63C760 (stub 0xFF20000); overlay flourish at 0x5F4681 (stub 0xFF30000)
[18:39:05.079] barks: bark hook at 0x5F2C90 (stub 0xFF40000)
[18:39:05.080] item-template spawns: item-template spawns at 0x52F748 (stub 0xFF50000)
[18:39:05.081] solo segment: RemovePartyMember calls at 0x541C1E (stub 0xFF60000)
[18:39:05.081] dialogue: 0x6A7110 (stub 0xFF70000); cinematic 0x6A7E20 (stub 0xFF80000)
[18:39:05.082] dialogue: dialogue skip authority at 0x6A7278 (stub 0xFF90000)
[18:39:05.082] combat: phantom-hit watch at 0x4DFA40 (stub 0xFFA0000)
[18:39:05.082] combat: phantom-effect watch at 0x4D8320 (stub 0xFFB0000)
[18:39:05.083] cinematic: mid-module movie watch at 0x602CF0 (stub 0xFFC0000)
[18:39:05.084] dialogue: dialogue gate at 0x4CF490 (stub 0xFFD0000)
[18:39:05.084] dialogue: forced-conversation tap at 0x57A470 (stub 0xFFE0000)
[18:39:05.085] container: container-use gate at 0x587420 (stub 0xFFF0000); container_lock_arbitration=1 (host-arbitrated)
[18:39:05.085] container: container-close watch at 0x587560 (stub 0x1B860000)
[18:39:05.086] dialogue: shot-camera guard at 0x6306E0 (stub 0x1B870000)
[18:39:05.087] minimap: peers on the minimap at 0x68ACB4 (stub 0x1B880000)
[18:39:05.087] pause: pause sanity check disarmed at 0x4C6503
[18:39:05.088] pause: pause button owned at 0x688590 and 0x6C0350 (stub 0x1B890000)
[18:39:05.089] death handling: control-swap guard at 0x5F6B60 (stub 0x1B8B0000); game-over guard at 0x4B6E99 (stub 0x1B8C0000)
[18:39:05.092] worker thread started
[18:39:05.166] agent: started KotorCoop.Agent.exe as pid 54264; log -> kotorcoop-agent.log
[18:39:05.502] menu: Co-op button shown, labelled and bound
[18:39:07.504] agent: liveness NeverLive -> Live
```

Every address in that list is the same VA the DRM-free build's own log prints, which is §4 end
to end: the stub decrypted `.text` in place before `DirectInput8Create` ran, and each site's
byte-pattern check matched the plaintext it expected.

**§6's question is settled in favour of the optimistic branch.** The Steam overlay does *not*
own the game's `SwapBuffers` IAT slot — `FrameHook`'s validation passed and it hooked
`0x73D00C` with the real `gdi32!SwapBuffers` (`0x779857F0`) still in it, so whatever the
overlay does, it is not an IAT rewrite of that import. And the hook fires: with a world loaded
the counter climbed 264,653 → 266,496 in four seconds. **No overlay change is needed.** The
"disable the overlay" instruction stays in the docs as a fallback for some future overlay
build, not as a requirement.

The stub does not re-verify after the OEP jump either (§3's INFERRED): the game ran for the
better part of an hour with ~25 inline detours live in `.text` and never faulted or reloaded.

⚠ `frames` reads **0 at the main menu, on both builds** — the swap hook is dormant there and
`MenuFrame` runs off the `MainLoop` call site instead. The plugin says so itself
(`WARNING: frame hook installed but has not fired for ~2s (normal while a movie or a module
load holds the main thread)`). Do not read a zero frame counter at the menu as an overlay
conflict; it is not one on either build.

### 18.3 The agent, attach, and the console (NEEDS-LIVE #1, continued)

```
pid 47028  block 0xFEE0000  version 60
  frames 264,653  phantoms 0  remotes 0  command 0
```

Block found by signature scan at version 60. `agent load --pid 47028 --load-save 1` staged and
loaded the demo save through the plugin ("loaded through the plugin (no input injection)"). A
console verb answers through the `FuncInterp` detour on the Steam process:

```
console: cmd serial 1  reply serial 11
last command: 'where'
reply:        'module END_M01AA  at 20.0 22.2 -1.3  hp 12/12'
```

### 18.4 The cross-store session (NEEDS-LIVE #2/#3, answered better than asked)

§17 asked for a Steam↔Steam session and a Steam↔DRM-free *refusal*. A second Steam instance is
impossible (§18.5), so the pairing run was the one that actually matters — and the refusal is
gone rather than reproduced: **host = GOG (`C:\GOG Games\…`, DRM-free), client = Steam**, both
on the same demo save, connected over loopback.

```
client [18:46:08] * connected - connected to 127.0.0.1:27140 as peer 2
client [18:46:08] * Lanik joined the game
host   [18:46:08] * Malak joined the game
host   [18:46:08] * co-op: connected, 1 player
```

No `RefusalReason.Executable`. **And no `W_UNCHECKED` either**, which is the other half of the
evidence: the executable check was *active* on both sides and *passed*, rather than quietly
skipped because a digest was missing. The warnings that did fire are the honest ones — this
particular Steam install carries a large mod pack, and none of these is fatal:

```
* co-op [W_APPEARANCE] appearance.2da differs; phantoms keep their default appearance
* co-op [W_TLK] dialog.tlk differs (different text mods); harmless
* co-op [W_OVERRIDE] override folders differ (2 vs 2665 files); gear or items unique to one side may not appear on the other
* co-op [W_2DA] 2DA edits differ between installs
* co-op [W_MODULES] module files differ; a module edited on one side may desync there
* co-op [W_GLOBALS] global variable tables differ; quest/world state and doors are not shared
```

That a *heavily modded* Steam install and a *vanilla* GOG install could share a session on the
strength of an identical running image, with every content difference degraded rather than
fatal, is a stronger result than a matched-pair test would have been.

Presence, both sides: `phantoms 1  remotes 1`, `peers 1`, `peers-here 1`.

**Module transition, and a world transfer across the store boundary:**

```
host   [18:52:53] * announcing transition to 'end_m01ab'
host   [18:52:56] * client announced transition to 'end_m01ab'
host   [18:53:03] * world sent (663,173 bytes)
host   where -> 'module end_m01ab  at 50.0 120.0 -0.0  hp 12/12'
client where -> 'module end_m01ab  at 50.0 120.0 -0.0  hp 12/12'
```

Both instances ended up in `end_m01ab` at the same coordinates, presence intact
(`peers-here 1`), and the GOG host shipped its world to the Steam client over the
world-transfer TCP path — 663 KB across two different stores' installs of the same game.

⚠ **The transition was driven by `warp`, not by a door, and that is a rig limitation rather
than a Steam one.** Three `usedoor end_door10` attempts on `END_M01AA` opened the door and then
stalled the walk short of the threshold:

```
door: 0x4A at 4.1m links to module 'end_m01ab' at waypoint 'from_01a'; open state 2
door: 600 frames after the walk the player is 2.7m from 0x4A (was 4.1m), at 46.6,117.6; aimed at 51.4,120.1.
      No transition happened - the door is still an obstacle, or the path stopped short.
```

`walkto` did not move the PC either, which is the known injected-input problem
(docs/live-verification.md, "Why `blockpoke walk` no-ops on the player character"). Nothing
about it is store-specific — same verb, same module, and the instance that stalled was the
DRM-free host. A real door crossing with a Steam client in the session is **still NEEDS-LIVE**;
everything the transition path does *after* the crossing is verified above.

### 18.5 A second Steam instance is impossible — confirmed (NEEDS-LIVE #4)

`agent launch --instance 2` renamed the mutex successfully (the name string lives at
`0x0073D75C` in `.rdata`, which the wrapper leaves in plaintext, so the patch itself works):

```
launching instance 2: C:\Program Files (x86)\Steam\steamapps\common\swkotor\swkotor.exe
launched pid 56096 with mutex name 'kcoop2'
error: Process was not started by this object, so requested information cannot be determined.
```

The process was created and then went away — the stub re-launches through the Steam client,
which enforces one copy of an app, and the launcher lost the pid it had just patched. Only the
original instance remained. **INFERRED is now VERIFIED.**

The workaround for a dev rig is simply ordering: **let the Steam copy take the plain mutex and
make the DRM-free copy instance 2.** That is what §18.4's run did — Steam launched first
through the client, then `agent launch --instance 2` on the GOG copy — and the host/client
roles come from the agents' arguments, not from launch order.

### 18.6 Steam invites, as far as one machine can take them

`steam_api64.dll` (Steamworks v1.62, copied from another Steam game on this machine for the
smoke only — **not committed and not packaged**, see §11) was dropped beside
`KotorCoop.Agent.exe` on the **GOG host**, which is the interesting direction: a GOG install
publishing an invite, needing only that the Steam account own app 32370.

```
steam: ready as app 32370 (ISteamFriends via SteamAPI_SteamFriends_v018)
steam: friends can now join this session (kc1/103.95.113.80:27140)
```

and, from Steam's own stderr, proof the client accepted the process:

```
Setting breakpad minidump AppID = 32370
SteamInternal_SetMinidumpSteamID:  Caching Steam ID:  76561…479 [API loaded no]
```

`steam_appid.txt` (5 bytes, `32370`) was written into the game directory by
`TryEnsureAppIdFile`, and only because a redistributable was present — `ComposeSteam`'s guard
against littering a non-Steam player's game folder works.

**Native calls exercised:** `NativeLibrary.TryLoad("steam_api64.dll")`; `SteamAPI_InitFlat`
(the v1.59+ signature — the older `SteamAPI_Init` fallback was not needed); export resolution
for `SteamAPI_Shutdown`, `SteamAPI_GetHSteamPipe`, `SteamAPI_ManualDispatch_Init` /
`_RunFrame` / `_GetNextCallback` / `_FreeLastCallback`,
`SteamAPI_ISteamFriends_SetRichPresence`, `_ClearRichPresence`, `_InviteUserToGame`;
`SteamAPI_ManualDispatch_Init`; the `SteamAPI_SteamFriends_v018` accessor; and
`SteamAPI_ISteamFriends_SetRichPresence("connect", …)` with a `SteamConnectStringRule` string
built from the host's STUN public address.

Note the interface version: §10 named `SteamAPI_SteamFriends_v017`; the facade probes v018
first, and **v018 is what a current client resolves**. The probe list is what saved it.

**NOT exercised** (both need a second machine and a second Steam account):
`SteamAPI_ISteamFriends_InviteUserToGame`, and the receive half — the manual-dispatch pump
delivering `GameRichPresenceJoinRequested_t` (callback 337) into `OnConnectRequest`.
`--steam-join` remains the only tested path into that code, and it is a plain CLI verb.

### 18.7 What §17's list looks like now

| | was | now |
| --- | --- | --- |
| 1. Steam build hooked, frame hook vs overlay | NEEDS-LIVE | **VERIFIED** — every hook installed, overlay loaded, IAT slot ours, hook fires (§18.2) |
| 2. Address parity end to end | NEEDS-LIVE | **VERIFIED** — same image digest, same hook addresses, a real session (§18.1, §18.4) |
| 3. Steam↔DRM-free | NEEDS-LIVE (watch it refuse) | **VERIFIED — it no longer refuses.** The fix is shipped and the session runs (§18.4) |
| 4. Second Steam instance | INFERRED impossible | **VERIFIED impossible**, with a rig workaround (§18.5) |
| §3 no post-OEP integrity checking | INFERRED | **VERIFIED** — ~25 live detours, a full session, no fault (§18.2) |
| §4 runtime `.text` parity | INFERRED | **VERIFIED** — identical image digest from two processes (§18.1) |
| A real DOOR crossing with a Steam client | — | **NEEDS-LIVE** — `usedoor`'s walk stalls on this module for rig reasons, on either build (§18.4) |
| `InviteUserToGame` and callback 337 | NEEDS-LIVE | **still NEEDS-LIVE** — needs a second machine and account (§18.6) |

## 19. Closing the reachability gap, and re-verifying against current code (2026-09-04)

Prompted by a request to make sure "the Steam invite approach works properly". Two problems,
found by reading the code rather than by guessing: `InviteUserToGame` had been implemented in
`SteamNative`/`SteamInviteService` since the original wave but **nothing in the codebase ever
called it** — no console verb, no CLI verb, no button. A player had no way to trigger the
explicit invite at all; the *only* working path was the passive one (a friend notices "Join
Game" on your entry because rich presence is published). And there was no way for a player, or
this mod, to learn a SteamID at all — `ISteamUser::GetSteamID` was never bound, so even a status
line could not say "you are logged in as ...".

**Both fixed this wave:**

- `SteamNative` now also binds `ISteamUser` (probing `SteamAPI_SteamUser_v023` → `_v021` →
  `_v020`, the same newest-first shape §10 already uses for `ISteamFriends`) and calls
  `GetSteamID`, exposed as `SteamNative.SteamId` / `SteamInviteService.SteamId64`.
- Two new console verbs (`ConsoleCommand.Steam.cs`, `CoopCommandDispatcher.Steam.cs`):
  - **`steamstatus`** — read-only: is the feature active, this account's SteamID64, and (while
    hosting) the exact connect string a friend receives. The one place a player can actually see
    whether the feature is working, instead of reading a log file.
  - **`steaminvite <steamid64>`** — the explicit push `InviteUserToGame` was always meant to
    reach (§10). No friend-list enumeration is bound (`ISteamFriends::GetFriendByIndex` etc.
    were never in scope), so a player types the number, copied from a friend's Steam profile
    ("Copy Player ID" or the URL's trailing digits) — a real limitation, noted in the verb's own
    error text rather than hidden.
  - Both share one `CurrentSteamConnectString()` helper with the passive publish, so the passive
    "Join Game" route and the explicit `steaminvite` route can never disagree about what a
    friend ends up connecting to.
- Tab completion (`ConsoleVerbCatalog.cs`) and the console help text list both verbs;
  `docs/CONSOLE.md`'s existing verb table now includes the connection-and-session and
  read-only rows for them (with a link to this section for the SteamID-typing limitation).

### 19.1 Live re-verification, this session, one machine/account (as before, §18.6)

`steam_api64.dll` (a current SDK build, copied locally from another owned Steam game for the
test only — not committed, not packaged, removed afterward, same as §18.6's own precedent) was
dropped beside the agent on the GOG install. Every native call this build makes was exercised:

```
steam: wrote steam_appid.txt (32370) beside the agent
steam: ready as app 32370, SteamID 76561197993781479 (ISteamFriends via SteamAPI_SteamFriends_v018,
       ISteamUser via SteamAPI_SteamUser_v023)
```

`SteamAPI_SteamUser_v023` — the first candidate in the new probe list — bound on the first live
attempt. `steamstatus` before and after `host`:

```
> steamstatus
steam: active, SteamID 76561197993781479, not hosting - nothing to invite anyone to yet
> host
steam: friends can now join this session (kc1/103.95.113.80:27140)
> steamstatus
steam: active, SteamID 76561197993781479, hosting - friends can Join Game or steaminvite this
       connect string: kc1/103.95.113.80:27140
```

**`InviteUserToGame` called for the first time ever in this project**, self-invited (the only
account available):

```
> steaminvite 76561197993781479
steam: invited 76561197993781479
```

The call reached Steam and returned success — no crash, no interop fault, the `byte` return
correctly marshalled and non-zero. This proves the ABI, the function-pointer resolution, and the
argument packing are all correct, which is everything this mod's side of the feature is
responsible for; whether a *distinct* friend's client actually receives and can act on the
invite still needs a second account (unchanged from §18.6 — see §19.3).

Disconnecting cleared the presence correctly (`steam: cleared the join-me presence`), matching
§18's original design.

### 19.2 The receive path, driven end to end (not just the CLI decode)

§18.6 called `--steam-join` "the only tested path", but that had only ever been checked as CLI
parsing (`SteamJoinOptionTests.cs`), never against a second, live instance actually connecting.
This time a second instance was launched with NO auto-started agent
(`auto_start_agent = 0`, restored after) and driven by hand exactly as Steam's own cold-start
launch would drive it:

```
KotorCoop.Agent.exe run --pid <p2> --kotor --steam-join 'kc1/103.95.113.80:27140'

steam: joining from an invite (kc1/103.95.113.80:27140)
* co-op: connecting...
connecting to 103.95.113.80:27140...
* no reply from 103.95.113.80:27140 - is UDP port 27140 forwarded on the host?
```

The connect string decoded to the exact right host and port and a real connection attempt was
made — the entirety of what this feature's code is responsible for. The failure that followed is
**NAT hairpinning, not a Steam-invite bug**: `103.95.113.80` is this same machine's own STUN-found
public address, and connecting *to your own public IP from inside your own LAN* is a router
capability most consumer routers do not have, wholly unrelated to Steam (`--join` on a hand-typed
public IP from the same LAN would fail identically). Proven by immediately repeating the exact
same `run --kotor` against the LISTEN endpoint instead:

```
KotorCoop.Agent.exe run --pid <p2> --kotor --join 127.0.0.1:27140

* connected - connected to 127.0.0.1:27140 as peer 2
* co-op: connected, 1 player
```

Clean connect, `peers-here 1`. So the whole pipeline a real cross-network invite depends on —
decode the connect string → resolve the address → dial → join the session — is now **verified
end to end** on current code, with the single remaining unknown being the same NAT/port-forwarding
question every internet game on this mod already has (`docs/research/network-topology.md`), not
anything specific to Steam.

### 19.3 What is still, honestly, NEEDS-LIVE

Only the thing that has needed two machines since §18.6 still needs two machines: **a distinct
Steam account receiving `GameRichPresenceJoinRequested_t` (callback 337) from a real friend's
"Join Game" click**, and (new this wave) **a distinct account being the target of `steaminvite`**
and seeing it arrive as an actual invite notification, not a same-account self-call. Nothing in
this session's testing could exercise either — there is one Steam account on this machine. If a
second tester with their own Steam account and a copy of KOTOR ever runs this build, that pairing
is the one thing left on the list.

### 19.4 Full regression check

`dotnet test` (whole suite, the one documented flaky exclusion aside): all green, no change from
before this wave's edits — the new `SteamId`/`ISteamUser` binding, the two console verbs, and
their verb-table/catalog/help-text wiring introduced no regressions elsewhere. New tests added:
`ConsoleCommandSteamTests` (parsing `steamstatus`/`steaminvite`, every malformed-SteamID shape)
and two new rows in `ConsoleCommandVerbTableCoverageTests`.


## 20. Startup forwarding follow-up (2026-10-24)

The follow-up found a real missing link: Steam launches the retail game, while
`AgentLauncher` always started the separate helper with `--menu` and discarded
the game's arguments. The plugin now parses the Windows process argument list,
accepts one strictly validated `kc1/` value (raw or after `+connect`/`--steam-join`),
and forwards it as `--steam-join` on the first helper launch. Ordinary launches,
malformed or ambiguous values and agent restarts retain menu behavior. An old
invitation is never replayed after an agent restart.

A startup invite now arms the same one-shot world-entry path as a live callback,
including save-mode joins, and re-arms it if the host is still loading and the
normal startup redial retries. It does not launch a duplicate character-adoption
task. The native callback path and cold-start bridge remain separate mechanisms.

The codec also refuses wildcard and broadcast destinations, including IPv4-mapped
IPv6 spellings of those addresses (confirmed accepted before the follow-up fix).
Valid mapped loopback addresses still round-trip. The normal host
listen socket binds `0.0.0.0`; that address must never be advertised to friends.
Without a usable discovered endpoint the optional Steam presence stays absent;
LAN browsing and direct connections still work.

Verification: **157 tests passed** using the filter
`FullyQualifiedName~Steam|FullyQualifiedName~CoopCommandDispatcherTests|FullyQualifiedName~JoinRedial`.
This includes real local UDP handshakes: the startup-invite entry point loads
exactly once after a matching-password acceptance, and never after rejection.
The retail-argument forwarding rule is tested through the actual agent option
parser. Tests do not substitute for Steam delivery from another account.

No `steam_api64.dll` was found in either installed KOTOR directory or anywhere
under this project (including ignored artifacts). No DLL was borrowed/downloaded,
no invitations were sent, and no second Steam account was available. Actual
Steam rich-presence publication and friend callback delivery remain unverified
on this build. A locally supplied game argument can verify the forwarding path,
but cannot establish that Steam itself delivered the argument.

The background-only live rig uses `Start-CoopBackgroundRig.ps1`: disposable GOG
copies start suspended on a named non-input Win32 desktop, then receive the
existing checked in-memory mutex rename. A hidden worker retains that desktop
handle until the exact owned game exits. No `SwitchDesktop` call or foreground
fallback exists. An explicit `-Steam` mode recognizes the original retail wrapper
and skips the entire mutex memory-access block; it requires Steam already running.
Only the disposable clone is changed, never an installed game.

The first menu GL captures were black on the inactive desktop. After displaying
the game window on that same non-input desktop and loading a world through the
plugin API, the rig owner obtained actual `END_M01AA` framebuffer pixels. No input
desktop switch or foreground fallback was used.

The first live cold-start trial exposed a separate guard in `RunCommand`: save-mode
joins refused to start at the main menu before the invite dispatcher could run.
Startup invites now pass that guard, while ordinary scripted save-mode joins keep
the loaded-world requirement. Regression coverage checks both cases.

With the corrected helper, a fresh GOG clone launched with the raw argument
`kc1/127.0.0.1:27150` automatically started the hidden helper, joined the host as
peer 2 and logged `auto-load: loaded through the plugin (no input injection)`.
No manual join save load was performed. On 2026-10-24 at 13:48 local time the join
was stable in `END_M01AA`, HP 6/6, one remote peer and one phantom; protocol 102
reported 5,997 frames. This verifies game-argument forwarding, same-PC transport
and save-mode world entry together. It does **not** verify Steam friend delivery.
The real helper process received `--steam-join` from the plugin in this test.
The separate standalone helper CLI was subsequently exercised against the actual
Steam executable as recorded in section 21.

A separate Steam clone was prepared from the installed game's resources, using
the existing stock `dialog.tlk.bak` (identical to the working rig's stock table),
fresh candidate mod assets and the corrected helper. Six retail root binaries
and resources, including the wrapped `swkotor.exe`, matched their installed-source
SHA-256 hashes. The retail executable retained entry RVA `0x0046D2ED` and the
`0xC0DEC0DE` wrapper marker. Installer checks passed. Initially a concurrent test
owned the stock `swkotor` mutex; the harness refused that conflict. After that
other test exited, the additional checks below became possible. No wrapped-executable
mutex bypass was attempted.

## 21. Exact-candidate Steam startup matrix (2026-10-24)

The disposable Steam clone received the verified beta.3 package built from source
`59e6e2b`. Its plain plugin SHA-256 was
`090FC46086F8E08E8770F6A4C3C8BA26E6141E434FF701B50121A5D4D34EFEDD`, and its
helper SHA-256 was `9CBD0AF5A5E7CEDDE35F837F8C935353F6AF880DC93CCACDE08F05C526A9E7F2`.
The original retail executable stayed byte-identical to the installed Steam copy.
All trials used the same non-input desktop, windowed configuration and already-running
Steam client. The controlled matrix also used `steam_appid.txt` containing `32370`
in the disposable clone, following Valve's [direct-launch identity guidance](https://partner.steamgames.com/doc/sdk/api).
No DLL was borrowed and no installed game, client configuration or registry setting changed.

| Layout | No game argument | Raw `kc1/127.0.0.1:27150` game argument |
| --- | --- | --- |
| Stock system DirectInput, mod DLL removed | Process remained alive/responding (PID 10260) | Access violation before mod startup (PID 49356) |
| Candidate plain NativeAOT plugin | Main menu and hidden helper started (PID 47016) | Same access violation (PID 40676) |
| Candidate native chain loader | Main menu and hidden helper started (PID 3948) | Same access violation (PID 20972) |

Stock without the mod also failed with `+connect kc1/127.0.0.1:27150` (PID 39720),
exit code `0xC0000005`. Windows Application event 1000 consistently recorded fault
RVA `0x0046D82F`, inside the retail `.bind` section. The machine's existing full
page heap (`GlobalFlag 0x02000000`, `PageHeapFlags 3`) and `verifier.dll` were present
in both successful and failed trials; their causal role is unproven.
The harness quotes the token as one Windows command-line argument. These are
direct clone launches, not captures of a real Steam-client-generated command line.

The initial comparison mixed argument-bearing and argument-free launches and therefore
did not isolate the install layout. The completed matrix corrected that interpretation.
A proposed automatic Steam chain-install change was fully reverted; neither changing
the wrapper nor changing machine diagnostics was necessary or attempted. Normal Steam
game startup with this candidate is verified. Retail-game argument startup remains
unresolved on this rig, including without the mod, and is not certified by the GOG
argument-forwarding result.

### Actual Steam game plus standalone helper join

The exact candidate's plain layout was restored. The wrapped Steam game started
with no game arguments, with only this disposable clone's `auto_start_agent=0`.
The helper was then launched hidden with
`run --pid <owned Steam PID> --kotor --steam-join kc1/127.0.0.1:27150`.
An initially empty save folder correctly reached the new-character flow after
connecting. For the save-mode check, a copy of the existing local baseline save
was staged before a fresh launch; no manual save load was issued.

At 14:10 local time, Steam game PID 36100 and helper PID 25652 connected as peer 3
to the two running GOG peers. The helper loaded through the plugin, then followed
the host to `danm13`. Protocol 102 showed travel serial/applied 1, status 0, two
remotes and two phantoms. Logs settled at `memory ok`, HP 6/6, malformed packets 0.
The front-buffer image `kotorcoop-shots/steamproof.png` visibly showed Bastila's
conversation and the connected co-op overlay on the non-input desktop.

This proves actual Steam-to-GOG local transport, the real helper CLI invite path,
automatic save loading and host-follow world entry. The differing save/area fixture
triggered `W_GIT` and `W_SAVE`, correctly disabling enemy, door, item and party
synchronization; this is not evidence of full shared-world equivalence. It also
does not establish retail-game argument startup, Steam friend invitations or
cross-network delivery. No native Steam API success was possible without the
optional library. Evidence remains in the disposable clone's separate
`steam-cli-helper.*.log`, `steam-cli-baseline.*.log` and screenshot files.

After closing the joined Steam pair, an additional no-argument trial without the
temporary `steam_appid.txt` stayed alive but did not produce fresh plugin startup
logs before it was closed. That direct-clone configuration is not verified as a
successful game startup. The file was restored for reproducibility; this does not
test normal Steam-client launch identity. All owned Steam test games and helpers
were then closed. The existing GOG capture pair was left for the rig owner to close.
