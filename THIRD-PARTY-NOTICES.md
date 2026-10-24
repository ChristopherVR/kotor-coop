# Third-party material

The project license covers only authorized original material. Third-party
software, fonts and game content retain their own rights and licenses; project
commercial/derivative restrictions do not replace upstream permissions.

## KotOR Patch Manager address database

`tools/kotor1_0_3.db` is an unchanged copy of Lane Dibello's upstream address
database: both Git blob IDs are `56ad035924d58a894b47e6573edfaf40e3c74d04`.
The current upstream MIT copyright and license are retained in
[Kotor-Patch-Manager.LICENSE.txt](third-party/Kotor-Patch-Manager.LICENSE.txt)
and the collected notice bundle. See [exact provenance](third-party/address-database-provenance.json)
for the matching historical revision and current license revision. The matched
historical revision did not contain LICENSE; this records the current upstream
license, not a claim that the original import already carried that notice.
The database is a private development reference and is not included in player
archives or the separate public repository. The upstream grant does not supply
rights in the game itself.

Restored agent metadata inspected on 2026-10-03 declares MIT for Terminal.Gui,
ColorHelper, JetBrains.Annotations, Onigwrap, System.IO.Abstractions and its
TestableIO packages, Testably.Abstractions, TextMateSharp/Grammars, Wcwidth and
Microsoft.Extensions packages; Markdig declares BSD-2-Clause. The self-contained
agent and NativeAOT plugin also contain .NET runtime components.

Full collected copyright/license texts are in [third-party/NOTICES.txt](third-party/NOTICES.txt),
which ships as KotorCoop.THIRD-PARTY-NOTICES.txt. The bundle includes upstream library
licenses, .NET 10.0.13 runtime and NativeAOT notices, Onigwrap notices, and the
recorded notices and available licenses for embedded grammars. Packaging verifies
its hashes and refuses dependency-version changes until the audit is updated.

Recovered the remaining nested notices: Microsoft's VS Code 1.50.0 notice bundle
and cgmanifest identify the exact cshtml 0.3.0 revision and James Summerton's MIT
notice. The assembly grammar matches the archived haxo.games source exactly after
newline/final-whitespace normalization; its MIT notice is included. All 21 themes
have a material hash and notice-source mapping, with full Microsoft/VS Code,
Atom, One Dark/Light conversion and Dracula notices included.
See [source resolutions](third-party/embedded-resource-provenance.json) and the original
[grammar provenance](third-party/grammar-origins.json). This addresses notice collection;
the game's rights, contributor authorization and custom-license legal review in
[LEGAL.md](docs/LEGAL.md) remain separate gates.

## Runtime audit refresh (2026-10-24)

The actual Release win-x64 agent dependency file and NativeAOT plugin restore
now use .NET 10.0.13. Compared the installed NuGet packages for 10.0.12 and
10.0.13: `Microsoft.NETCore.App.Runtime.win-x64`'s `LICENSE.TXT` is byte-identical
(SHA-256 `D7A68596AB69B06F51CA278A6545148E4269A9381C26D597C13DF5D88E08CF5B`).
Its `THIRD-PARTY-NOTICES.TXT`, and those in both `Microsoft.DotNet.ILCompiler`
and `runtime.win-x86.Microsoft.DotNet.ILCompiler`, are also byte-identical
(SHA-256 `6D15E10A101C6BFFF2AB4429ED061BF76C456FC4B23AD6B03E0D0F8377148A21`).
Those exact bodies are already present in the collected bundle. Only their
four version labels changed; all other notice bytes, including the KotOR Patch
Manager notice, were preserved. Both compiler packages declare MIT in their
10.0.13 NuGet metadata; they do not contain a separate `LICENSE.TXT` file.
The packaging manifest and NativeAOT guard now require the observed versions.
This refresh verifies license materials and dependency identity, not malware
status or a fresh vulnerability audit.

## Candidate dependency inventory (2026-10-24)

The current agent restore/build uses these versions. This is a version inventory,
not a grant of rights or a replacement for upstream copyright and license texts.

| Component | Version |
| --- | --- |
| Terminal.Gui | 2.5.0 |
| TextMateSharp and TextMateSharp.Grammars | 2.0.4 |
| Onigwrap | 1.0.11; native assets explicitly excluded by the agent project |
| ColorHelper | 1.8.1 |
| JetBrains.Annotations | 2026.2.0 |
| Markdig | 1.3.2 |
| System.IO.Abstractions and TestableIO wrappers | 22.2.0 |
| Testably.Abstractions.FileSystem.Interface | 10.3.0 |
| Wcwidth | 4.0.1 |
| Microsoft.Extensions packages | 10.0.11, except Logging.Abstractions 10.0.7 |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.13 |
| Microsoft.DotNet.ILCompiler and runtime.win-x86.Microsoft.DotNet.ILCompiler | 10.0.13 |
| Microsoft.NET.ILLink.Tasks (build tooling) | 10.0.13 |

NuGet's earlier solution-wide audit with transitive dependencies reported no known
vulnerable packages on 2026-10-03. It does not cover undisclosed vulnerabilities,
game hooks, native implementation defects or fulfillment of notice obligations.
Verify the final self-contained runtime and embedded materials separately.

DejaVu fonts carry their [included license](media/trailer/fonts/LICENSE.txt).
Game music, voices, logo and imagery are not licensed by the project; see
[trailer provenance](media/trailer/README.md) and the legal release gate.
