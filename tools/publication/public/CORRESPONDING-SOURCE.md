# Corresponding source

Every official GPL player download must have a matching project source archive,
`CORRESPONDING-SOURCE.md` and `corresponding-source.json` beside the Windows ZIP.
Use the archive for that exact release rather than the moving default branch.
The source includes original mod code, checked-in generated assets, their
generators, and the scripts that control compilation and installation.

The machine-readable release file identifies every bundled managed dependency,
the self-contained .NET runtime and the NativeAOT runtime source. It gives exact
upstream commits and source archive download URLs, including dependencies whose
source is hosted by their upstream projects. Those source downloads are available
without charge. Package and source identities are checked before publication;
the project remains responsible for keeping the offered source available.

The full upstream license notices remain in the player ZIP. Their independent
licenses continue to apply. A notice bundle alone is not the corresponding source.
The source inventory records general, unmodified build tools separately from
linked player components. Players do not need the compiler or generator tools.
The game and optional Steam API DLL are not included in either archive.

See [GNU GPL version 3, section 6](https://www.gnu.org/licenses/gpl-3.0.html#section6)
for object-code source-delivery terms. The release-specific section below and its
JSON companion provide the immutable project revision and dependency directions.
Report an unavailable source link through the public repository so it can be
restored. Do not treat a newer source tree as a replacement for missing release source.
