# Third-party material

The project license covers only authorized original material. Third-party
software, fonts and game content retain their own rights and licenses; project
commercial/derivative restrictions do not replace upstream permissions.

Restored agent metadata inspected on 2026-10-03 declares MIT for Terminal.Gui,
ColorHelper, JetBrains.Annotations, Onigwrap, System.IO.Abstractions and its
TestableIO packages, Testably.Abstractions, TextMateSharp/Grammars, Wcwidth and
Microsoft.Extensions packages; Markdig declares BSD-2-Clause. The self-contained
agent and NativeAOT plugin also contain .NET runtime components.

This inventory is not a substitute for required copyright/license texts or
nested notices. Before public release, verify exact published versions and ship
full upstream notices, including embedded grammar and native-library licenses.
The audit remains an explicit [legal release gate](docs/LEGAL.md).

## Candidate dependency audit (2026-10-03)

The r15 agent restore/build uses these versions. This is a version inventory,
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
| Microsoft.NET.ILLink.Tasks (build tooling) | 10.0.12 |

NuGet's solution-wide audit with transitive dependencies reported no known
vulnerable packages on this date. It does not cover undisclosed vulnerabilities,
game hooks, native implementation defects or fulfillment of notice obligations.
Verify the final self-contained runtime and embedded materials separately.

DejaVu fonts carry their [included license](media/trailer/fonts/LICENSE.txt).
Game music, voices, logo and imagery are not licensed by the project; see
[trailer provenance](media/trailer/README.md) and the legal release gate.
