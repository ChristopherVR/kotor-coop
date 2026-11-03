#!/usr/bin/env bash
# Installer-only regression: no Wine or game required.
set -euo pipefail
installer="$(cd "$(dirname "$0")" && pwd)/install.sh"
root="$(mktemp -d)"
trap 'rm -rf -- "$root"' EXIT
pkg="$root/package"
mkdir -p "$pkg/chain-loader" "$pkg/Override"
printf '=\0=\0=\0 \0K\0o\0t\0o\0r\0C\0o\0o\0p\0 \0p\0l\0u\0g\0i\0n\0 \0l\0o\0a\0d\0e\0d\0' > "$pkg/dinput8.dll"
printf 'KotorCoop.Loader chain dinput8' > "$pkg/chain-loader/dinput8.dll"
printf MZ > "$pkg/KotorCoop.Agent.exe"
printf '[coop]\n' > "$pkg/coop.ini"
for mode in fresh migrate conflict; do
    game="$root/$mode"
    mkdir -p "$game"
    printf MZ > "$game/swkotor.exe"
    printf 'IsUltimateASILoader' > "$game/dinput8.dll"
    printf 'user asi' > "$game/example.asi"
    if [ "$mode" = migrate ]; then
        mv "$game/dinput8.dll" "$game/dinput8.prev.dll"
        cp "$pkg/chain-loader/dinput8.dll" "$game/dinput8.dll"
        printf 'KotorCoop.Plugin.dll\ndinput8.prev.dll\n' > "$game/dinput8.chain"
    elif [ "$mode" = conflict ]; then
        printf 'foreign hooked proxy' > "$game/dinput8Hooked.dll"
        if bash "$installer" --from "$pkg" --game-dir "$game" > "$root/output" 2>&1; then
            echo 'FAIL: overwritten original-library slot'; exit 1
        fi
        test "$(cat "$game/dinput8.dll")" = IsUltimateASILoader
        test "$(cat "$game/dinput8Hooked.dll")" = 'foreign hooked proxy'
        test ! -f "$game/KotorCoop.Plugin.dll"
        continue
    fi
    for pass in 1 2; do
        bash "$installer" --from "$pkg" --game-dir "$game" > "$root/output"
        bash "$installer" --check --game-dir "$game" > "$root/output"
        grep -q 'Ultimate ASI Loader host' "$root/output"
        test "$(cat "$game/dinput8.dll")" = IsUltimateASILoader
        cmp "$pkg/chain-loader/dinput8.dll" "$game/dinput8Hooked.dll"
        cmp "$pkg/dinput8.dll" "$game/KotorCoop.Plugin.dll"
        test ! -f "$game/dinput8.prev.dll"
    done
    bash "$installer" --uninstall --game-dir "$game" > "$root/output"
    test "$(cat "$game/dinput8.dll")" = IsUltimateASILoader
    test -f "$game/example.asi"
    test ! -f "$game/dinput8Hooked.dll"
done
echo 'PASS: ASI host install, migration, upgrade, uninstall, and original-slot refusal'
