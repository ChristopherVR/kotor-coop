# Licensing and legal notes

This page explains how the project is licensed, what it does and does not include, and which rights questions stay open. It is not legal advice.

## License

The project's own code and original assets are released under the **GNU General Public License v3.0 only** (see [LICENSE](../LICENSE)). Derivatives of the mod must stay open source under the same terms. Third-party components keep their own licenses; see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) and [third-party/NOTICES.txt](../third-party/NOTICES.txt).

The author has confirmed this is an independent project with no outside ownership or conflicting prior grants over the original code and assets. That covers the original material only. It grants no rights in the game, in dependencies, or in anyone else's work.

## Not affiliated with the game's rights holders

This is an unofficial fan project. It is not endorsed by or affiliated with Lucasfilm, Disney, LucasArts, BioWare, Electronic Arts, Aspyr or any other holder of rights in the game. No publisher permission has been sought or claimed.

Every player needs their own lawfully acquired, properly licensed copy of the game. The project license gives no license to the game and no permission to distribute game copies, keys, cracks or DRM bypasses. Game assets and trademarks remain their owners' property.

## What is and isn't in the repository

The project is developed and released from this repository. Releases are built from tags by GitHub Actions. The repository and the player package contain no game files: no extracted game music, voices, textures or models.

The boss's procedural Cairn Sentinel geometry, animation and synthesized audio are original. Generated GFF blueprints refer to stock field layouts and defaults instead of embedding game media or compiled scripts. The address database used during development is not part of the player package.

The promotional trailer contains captured game picture, dialogue and ambience, plus separately licensed music. Its rights position is separate from the source code and the binary release.

## Game and store terms

Modifying a commercial game can conflict with the terms that come with it. Those terms are not the same for every copy, so this project makes no claim that they permit a multiplayer mod.

- **Steam:** the [KOTOR 1 EULA](https://store.steampowered.com//eula/32370_eula_0) shows general Disney terms that restrict modification, derivative works, reverse engineering and bypassing protections. The [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/) separately covers modification and tampering, subject to applicable exceptions.
- **GOG:** the [KOTOR page](https://www.gog.com/en/game/star_wars_knights_of_the_old_republic) requires Disney terms. The [GOG User Agreement](https://support.gog.com/hc/en-us/articles/212632089-GOG-User-Agreement?product=gog) says derivative rights depend on the content owner (§11.1(b)) and addresses mods, injection and private servers (§§11.1(e)–(f)). DRM-free does not mean unrestricted modding permission.
- **Footage:** no reviewed policy gives affirmative permission for a KOTOR multiplayer mod or for promotional footage of it. Kevin MacLeod's "Heroic Age" is used under CC BY 4.0, with credit and the full license text. That license covers the music only, not the game.
- **Interoperability:** in Australia, [Copyright Act 1968 §§47D–47H](https://www.legislation.gov.au/C1968A00063/latest/text) allow some program-interoperability work and protect those exceptions from contractual exclusion. They depend on specific conditions and do not clear distributing audiovisual material or use in other countries.

These are real residual risks, not a finding that every modification is unlawful. A working hook, a lawful purchase, a disclaimer or community tolerance does not amount to permission. If you need certainty, take advice or ask the rights holders.

## GPLv3 and the game

The plugin loads inside the proprietary game and calls its internal functions, so it can be seen as a combined work with the game. [GNU's plugin guidance](https://www.gnu.org/licenses/gpl-faq.en.html#GPLPluginsInNF) explains how a GPL plugin and a non-free host can conflict, and how the plugin's copyright holders can add a linking permission.

The project adds a narrow [KOTOR 1 linking permission](../KOTOR-LINKING-EXCEPTION.txt) under GPLv3 section 7. It lets the authorizing holders' mod code combine with a lawfully acquired KOTOR 1 engine without supplying the engine's source, and it keeps the mod's own source obligations. It covers only contributions whose holders agree to it. It grants no rights in the game, its assets or trademarks, no right to redistribute the game, no DRM bypass, and no permission to breach third-party terms. Windows system libraries are treated as GPLv3 already treats them; other proprietary libraries are not exempted.

## Corresponding source

For each binary release, [GPLv3 §§1 and 6(d)](https://www.gnu.org/licenses/gpl.en.html) require access to the exact Corresponding Source: the mod source, relevant interfaces, build and install scripts, and the source of embedded dependencies, subject to the license's exclusions. Each release therefore comes with an identified, immutable source snapshot. [CORRESPONDING-SOURCE.md](CORRESPONDING-SOURCE.md) describes how to obtain it.

The agent embeds a self-contained .NET runtime and managed dependencies, and the NativeAOT plugin embeds parts of the runtime. The bundled MIT, BSD, TextMate and Apache components show no known incompatibility with GPLv3.

## Package notes

- Valve's optional Steam DLL is not part of the player package. Owning another game gives no right to reuse it. The [Steamworks SDK agreement](https://partner.steamgames.com/documentation/sdk_access_agreement/) sets the terms for redistributing it.
- GitHub's [terms](https://docs.github.com/en/site-policy/github-terms/github-terms-of-service#5-license-grant-to-other-users) let others view and fork the public repository. Your GPL rights downstream are unchanged.
- No scan, test or document in this repository is legal clearance. Current technical checks are in [RELEASE-READINESS.md](RELEASE-READINESS.md).
