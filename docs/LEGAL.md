# Rights, licensing and release status

## Current decision — 24 October 2026

The owner selected **GNU GPL version 3** for open-source distribution so distributed derivatives remain open source. The implementation uses GPL-3.0-only. The selected standard license, copyright notice and contribution/public/package descriptions must agree before publication; the superseded Play and Contribute restrictions must not remain extra noncommercial or no-fork conditions.

On 24 October 2026, in response to a question explicitly covering ownership and authorization to distribute all original code/assets, including earlier work, employer/outside ownership and prior grants, the owner confirmed: **“Yes—my independent project; no outside ownership or prior conflicting grants”.** This closes the original-material ownership/authorization gate on that factual representation. It is not an independent audit or a grant of rights in the game, dependencies or other people's material. No publisher permission was claimed. Handle any specific later-discovered imported work or conflicting agreement on its facts.

This unofficial fan project claims no endorsement by or affiliation with Lucasfilm, Disney, LucasArts, BioWare, Electronic Arts, Aspyr or other game rights holders. Each player needs a lawfully acquired, properly licensed game. The code license supplies no game license or permission to distribute game copies, keys, cracks or DRM bypasses. Game assets and trademarks retain their owners' rights; separately licensed material retains its own terms.

## Distribution

The project is developed and released from a single public repository, `ChristopherVR/kotor-coop`.
Releases are built from tags by GitHub Actions. The repository contains no game files, extracted game
music, voices, textures or models. Earlier private development used a separate repository with
research recordings and game-derived media; that material was left out when the repository was made
public and its history was not carried over.

The current trailer contains captured game picture, dialogue and ambience as well as separately
licensed music; its rights question is distinct from the source and binary release.

The boss's procedural Cairn Sentinel geometry, animation and synthesized nonverbal audio replace prior character assets. Generated GFF blueprints record their stock field-layout/default references rather than embedding game media or compiled scripts. The address database has recorded exact-byte provenance and the current upstream MIT notice; it is not in the player ZIP. These controls do not establish permission for the mod as a whole.

## Identified game/store and media uncertainty

- **Steam:** the [KOTOR 1 EULA link](https://store.steampowered.com//eula/32370_eula_0) currently renders general Disney terms restricting modification, derivatives, reverse engineering and bypassing functions/protections. The [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/) separately addresses modification and execution tampering, subject to applicable exceptions. A working hook or lawful purchase is not affirmative multiplayer-mod permission.
- **GOG:** its [KOTOR page](https://www.gog.com/en/game/star_wars_knights_of_the_old_republic) requires Disney terms. The [GOG User Agreement](https://support.gog.com/hc/en-us/articles/212632089-GOG-User-Agreement?product=gog) says derivative rights depend on the content rights holder (§11.1(b)) and broadly addresses mods/injection/private servers (§§11.1(e)–(f)); §2.2 addresses conflicting game EULAs. DRM-free play is not unrestricted modding permission. Current webpages do not establish every purchaser's historical accepted or territorial terms.
- **Game footage:** no reviewed primary policy establishes affirmative permission for this KOTOR multiplayer mod or promotional footage. Process-audio capture does not clear underlying rights. Kevin MacLeod's “Heroic Age” has track-specific CC BY 4.0 evidence, credits, changes and full license text; that grant covers the music, not game IP. Disclaimers, free distribution and community tolerance do not supply consent.
- **Interoperability:** [Copyright Act 1968 §§47D–47H](https://www.legislation.gov.au/C1968A00063/latest/text) provide conditional program-interoperability protections and protect specified exceptions from contractual exclusion. Lawful copies, unavailable interface information, necessary copying and permitted use of resulting information matter. No finding establishes all conditions; these provisions do not clear audiovisual distribution or worldwide use.

These are material residual rights/contract risks, not proof that every modification is unlawful. An informed release decision must distinguish confirmed controls from absent permission evidence and unresolved legal application. If affirmative rights-holder clearance is required, that evidence has not been obtained. Seek targeted professional advice or rights-holder permission where needed; no general legal requirement for a lawyer certificate or custom-license sign-off is asserted.

## GPLv3: game coupling and corresponding source

The NativeAOT plugin loads inside the proprietary game and calls internal engine functions using shared engine structures. This merits a specific combined-work/linking assessment rather than an assumption of mere aggregation. [GNU's plugin guidance](https://www.gnu.org/licenses/gpl-faq.en.html#GPLPluginsInNF) describes how a GPL plugin and nonfree host can raise a combined-program conflict and how a copyright-holder-authorized additional linking permission can address the plugin-license side. That guidance is not a court finding that this particular mod is unlawful.

On 24 October 2026 the owner approved the exact narrow [KOTOR 1 linking permission](../KOTOR-LINKING-EXCEPTION.txt) under GPLv3 section 7. Its installed text matches the reviewed draft after whitespace normalization. It permits the authorizing copyright holders' covered mod material to combine with a lawfully acquired KOTOR 1 engine without supplying that engine's source; it preserves the mod's Corresponding Source obligations and covers only contributions whose holders authorize it. It grants no publisher permission, game/assets/trademark rights, redistribution right in the game, DRM bypass or permission to breach third-party terms. Windows System Libraries retain GPLv3's existing treatment; arbitrary proprietary libraries are not exempted. Release notices and contribution terms must retain that precise scope.

For a downloadable GPL object-code release, [GPLv3 §§1 and 6(d)](https://www.gnu.org/licenses/gpl.en.html) require access to the exact Corresponding Source: mod source, relevant interfaces, build/install scripts and covered embedded dependency source, subject to the license's exclusions. A moving main branch, private commit hash or notice bundle alone is insufficient. Offer a clearly identified immutable public source snapshot beside each binary; map actual embedded components to exact source revisions and equivalent no-charge downloads, and maintain their availability. A dependency-source bundle or mirror can make that obligation more robust.

The agent embeds a self-contained .NET runtime and managed dependencies; the NativeAOT plugin embeds runtime portions. The reviewed MIT/BSD/TextMate/Apache materials reveal no concrete GPLv3 incompatibility, but source availability is a new requirement beyond preserving notices. Do not assume every embedded runtime component is a System Library. The installed .NET 10.0.13 runtime/ILCompiler metadata names dotnet/dotnet commit 4099f83977f4d29604a9aeb971fb0a7bc1d5f699. Resolve remaining package source identities against the final package. General unmodified SDK/compiler tools and automatically regenerable intermediates need not all be committed; retain their versions and the scripts needed to regenerate the work. See the dated review for the practical source-delivery plan.

## Final license and package checks

- Apply the selected GPLv3 terms and the approved narrow KOTOR 1 linking permission consistently. Remove superseded commercial/independent-release restrictions; preserve upstream licenses and notices. The owner's permission addresses the mod-license side only; publisher/store/media uncertainty remains.
- Validate the exact source export and final binary against the [dependency inventory](../THIRD-PARTY-NOTICES.md), [notice bundle](../third-party/NOTICES.txt), provenance/version guards and Corresponding Source record. Recovered notices and the runtime refresh do not certify arbitrary later outputs.
- Keep Valve's optional DLL outside the player package. Owning another game does not establish DLL reuse or KOTOR AppID authorization. The [Steamworks SDK agreement](https://partner.steamgames.com/documentation/sdk_access_agreement/) permits certain licensee redistribution subject to its terms, without promising service access.
- Preserve [GitHub public viewing/forking permissions](https://docs.github.com/en/site-policy/github-terms/github-terms-of-service#5-license-grant-to-other-users), GPL downstream rights and separately licensed media rights. Repository administration does not assign copyright.

The former mandatory counsel/custom-license gate is superseded by this dated evidence, ownership confirmation and open-source decision. Current technical checks and explicitly unverified beta scope are in [RELEASE-READINESS.md](RELEASE-READINESS.md). No scan, test, source export or document here is represented as legal clearance.
