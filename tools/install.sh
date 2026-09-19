#!/usr/bin/env bash
# kotor-coop installer for Linux / Steam Deck — the game runs under Proton or Wine, this
# script runs natively, so no Wine is needed to install. It mirrors what the Windows installer
# (KotorCoop.Agent.exe install, src/KotorCoop.Agent/PackageInstaller.cs) does, file for file:
#
#   bash install.sh                                   # find the game, install, print the launch option
#   bash install.sh --game-dir ~/Games/Heroic/swkotor # a specific install
#   bash install.sh --check                           # report what is installed, change nothing
#   bash install.sh --uninstall                       # remove what install placed, keep coop.ini
#
# The layout it produces is the one the Windows installer produces, so `KotorCoop.Agent.exe
# install --check` run inside the prefix agrees with `install.sh --check`:
#   plain:  dinput8.dll is the plugin
#   chain:  another mod already owned dinput8.dll -> our loader takes the slot, the plugin is
#           KotorCoop.Plugin.dll, the other mod is kept as dinput8.prev.dll, dinput8.chain lists
#           the order. A plain copy of Windows' own dinput8.dll is not a mod and is replaced.
#
# What this script cannot do for you is the ONE Proton-specific step: Wine ships its own
# dinput8 and prefers it over ours unless the game is launched with
#   WINEDLLOVERRIDES="dinput8=n,b" %command%
# It prints that at the end, tailored to where it found the game. docs/research/proton.md.
#
# One thing the Windows installer does that this script deliberately does not: it adds a
# Windows Firewall inbound rule for KotorCoop.Agent.exe (WindowsFirewall.cs). There is no
# Windows Firewall inside a Proton prefix; if your distribution runs one (ufw, firewalld), a
# HOST has to open the session UDP port (27140 by default; plus 27016 for the LAN browser)
# there by hand. A Steam Deck ships with no firewall.

set -eu

PLUGIN_MARKER='=.=.=. .K.o.t.o.r.C.o.o.p. .p.l.u.g.i.n. .l.o.a.d.e.d'   # UTF-16LE, '.' = the NUL
LOADER_MARKER='KotorCoop.Loader chain dinput8'
MICROSOFT_MARKER='M.i.c.r.o.s.o.f.t. .C.o.r.p.o.r.a.t.i.o.n'            # UTF-16LE

package="$(cd "$(dirname "$0")" && pwd)"
game=''
mode='install'
force=0
yes=0

usage() { sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
    case "$1" in
        --game-dir|--game-path) game="$2"; shift 2 ;;
        --game-dir=*|--game-path=*) game="${1#*=}"; shift ;;
        --from) package="$2"; shift 2 ;;
        --check) mode='check'; shift ;;
        --uninstall) mode='uninstall'; shift ;;
        --force) force=1; shift ;;
        -y|--yes) yes=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

# --- identify a dinput8.dll the way PackageInstaller.Identify does --------------------------
has_marker() { LC_ALL=C grep -a -q -e "$2" "$1" 2>/dev/null; }
identify() {
    if [ ! -f "$1" ]; then echo none
    elif has_marker "$1" "$LOADER_MARKER"; then echo loader
    elif has_marker "$1" "$PLUGIN_MARKER"; then echo plugin
    else echo foreign
    fi
}
foreign_name() {
    if has_marker "$1" "$MICROSOFT_MARKER"; then echo system
    elif has_marker "$1" 'ReShade'; then echo 'ReShade'
    elif has_marker "$1" 'Ultimate ASI Loader'; then echo 'Ultimate ASI Loader'
    else echo 'another dinput8 mod'
    fi
}

# --- find the game --------------------------------------------------------------------------
# Native Linux paths: Steam (including the Deck's SD card via libraryfolders.vdf, and the
# Flatpak client), Heroic and Lutris. The same list PackageInstaller walks as Z:\ from inside a
# prefix (src/KotorCoop.Agent/ProtonPaths.cs).
steam_roots() {
    for r in "$HOME/.steam/steam" "$HOME/.steam/root" "$HOME/.local/share/Steam" \
             "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
        [ -d "$r" ] && echo "$r"
    done
    return 0
}
steam_libraries() {
    steam_roots | while read -r root; do
        echo "$root"
        vdf="$root/steamapps/libraryfolders.vdf"
        [ -f "$vdf" ] && sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf"
    done | awk '!seen[$0]++'
}
candidates() {
    [ -n "${STEAM_COMPAT_INSTALL_PATH:-}" ] && echo "${STEAM_COMPAT_INSTALL_PATH}|Steam (Proton)"
    steam_libraries | while read -r lib; do echo "$lib/steamapps/common/swkotor|Steam library"; done
    for d in "$HOME"/Games/Heroic/* "$HOME"/Games/*; do
        [ -d "$d" ] && echo "$d|Heroic/Lutris folder"
    done
    return 0
}

if [ -z "$game" ]; then
    found=()
    labels=()
    while IFS='|' read -r dir label; do
        [ -f "$dir/swkotor.exe" ] || continue
        dir="$(cd "$dir" && pwd)"
        skip=0
        for f in ${found[@]+"${found[@]}"}; do [ "$f" = "$dir" ] && skip=1; done
        [ $skip = 1 ] && continue
        found+=("$dir"); labels+=("$label")
    done < <(candidates)

    if [ ${#found[@]} -eq 0 ]; then
        echo "no KOTOR install found (looked in the Steam libraries, ~/Games/Heroic and ~/Games)." >&2
        echo "pass it: bash install.sh --game-dir /path/to/swkotor   (the folder holding swkotor.exe)" >&2
        exit 1
    elif [ ${#found[@]} -eq 1 ]; then
        game="${found[0]}"
        echo "found the game: $game (${labels[0]})"
    else
        echo "found more than one install:"
        for i in "${!found[@]}"; do echo "  $((i + 1))) ${found[$i]} (${labels[$i]})"; done
        if [ $yes = 1 ] || [ ! -t 0 ]; then
            echo "pass one with --game-dir" >&2; exit 1
        fi
        printf 'which one? [1] '
        read -r pick
        pick="${pick:-1}"
        game="${found[$((pick - 1))]}"
    fi
fi

[ -d "$game" ] || { echo "game directory not found: $game" >&2; exit 1; }
game="$(cd "$game" && pwd)"
[ -f "$game/swkotor.exe" ] || echo "warning: no swkotor.exe in $game - is this really the game directory?"

proxy="$game/dinput8.dll"
chain_plugin="$game/KotorCoop.Plugin.dll"
chain_file="$game/dinput8.chain"
previous="$game/dinput8.prev.dll"

# --- check: PASS/FAIL lines, exit status = all passed (same lines as install --check) -------
check() {
    ok=0
    report() { if [ "$1" = 0 ]; then echo "PASS: $2"; else echo "FAIL: $2"; ok=1; fi; }
    kind="$(identify "$proxy")"
    case "$kind" in
        loader) layout=chain ;;
        plugin) layout=plain ;;
        foreign) layout=foreign-only ;;
        *) if [ -f "$chain_plugin" ] || [ -f "$chain_file" ]; then layout='chain (dinput8.dll missing)'; else layout=none; fi ;;
    esac
    echo "layout: $layout ($proxy)"
    case "$kind" in
        none) report 1 "no dinput8.dll in $game - kotor-coop is not installed here" ;;
        foreign) report 1 "$proxy belongs to another mod - kotor-coop is not installed here (run install to chain behind it or --force to overwrite it)" ;;
        plugin) report 0 "$proxy carries the kotor-coop plugin marker" ;;
        loader)
            report 0 "$proxy carries the chain loader marker"
            if [ -f "$chain_plugin" ]; then
                report 0 "$chain_plugin is present"
                if [ "$(identify "$chain_plugin")" = plugin ]; then report 0 "$chain_plugin carries the kotor-coop plugin marker"
                else report 1 "$chain_plugin carries the kotor-coop plugin marker"; fi
            else
                report 1 "$chain_plugin is MISSING"
            fi
            if [ -f "$chain_file" ]; then
                n=0
                while read -r entry; do
                    n=$((n + 1))
                    case "$entry" in /*) p="$entry" ;; *) p="$game/$entry" ;; esac
                    if [ -f "$p" ]; then report 0 "chain entry '$entry' exists ($p)"; else report 1 "chain entry '$entry' exists ($p)"; fi
                done < <(chain_entries)
                if [ $n -gt 0 ]; then report 0 "$chain_file lists at least one entry"; else report 1 "$chain_file lists at least one entry"; fi
            else
                report 1 "$chain_file is MISSING"
            fi ;;
    esac
    if [ -f "$game/coop.ini" ]; then report 0 "$game/coop.ini exists"; else report 1 "$game/coop.ini is MISSING"; fi
    if [ -f "$game/KotorCoop.Agent.exe" ]; then report 0 "$game/KotorCoop.Agent.exe is present"; else report 1 "$game/KotorCoop.Agent.exe is MISSING"; fi
    return $ok
}
chain_entries() {
    [ -f "$chain_file" ] || return 0
    sed -e 's/\r$//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' "$chain_file" | grep -v -e '^$' -e '^#' -e '^;' || true
}

# --- uninstall: only what install placed; restores a kept foreign dinput8.dll ---------------
uninstall() {
    case "$(identify "$proxy")" in
        none) echo "nothing installed at $proxy" ;;
        foreign) echo "left $proxy alone: it is not ours" ;;
        *) rm -f "$proxy"; echo "removed $proxy" ;;
    esac
    [ -f "$chain_file" ] && { rm -f "$chain_file"; echo "removed $chain_file"; }
    if [ -f "$previous" ] && [ ! -f "$proxy" ]; then
        mv "$previous" "$proxy"; echo "restored the other mod's dinput8.dll from dinput8.prev.dll"
    fi
    for f in kotorcoop-plugin.log dinput8-chain.log; do
        [ -f "$game/$f" ] && { rm -f "$game/$f"; echo "removed $game/$f"; }
    done
    for f in "$game"/KotorCoop.*; do
        [ -e "$f" ] && { rm -f "$f"; echo "removed $f"; }
    done
    for f in "$game"/Override/k_coop_*; do
        [ -e "$f" ] && { rm -f "$f"; echo "removed override $f"; }
    done
    return 0
}

case "$mode" in
    check) check; exit $? ;;
    uninstall) uninstall; exit 0 ;;
esac

# --- install ---------------------------------------------------------------------------------
[ -f "$package/dinput8.dll" ] || { echo "dinput8.dll not found in $package - run this from an extracted kotor-coop package" >&2; exit 1; }
[ -f "$package/KotorCoop.Agent.exe" ] || { echo "KotorCoop.Agent.exe not found in $package - run this from an extracted kotor-coop package" >&2; exit 1; }

if [ "$package" = "$game" ]; then
    echo "package and game directory are the same ($game); the files are already in place"
    check; exit $?
fi

existing="$(identify "$proxy")"
chain=0
if [ -f "$chain_file" ] || [ -f "$chain_plugin" ] || [ "$existing" = loader ]; then chain=1; fi
chained_name=''

if [ "$existing" = foreign ] && [ $force = 0 ]; then
    name="$(foreign_name "$proxy")"
    if [ "$name" = system ]; then
        echo "$proxy is the system dinput8.dll (Microsoft Corporation) - not a mod, so it is simply replaced (no chain needed)"
    else
        chain=1; chained_name="$name"
        echo "found another mod in dinput8.dll ($name); installing as a chain: our loader takes dinput8.dll, the other mod is kept as dinput8.prev.dll and loaded after us"
    fi
elif [ $chain = 1 ]; then
    echo "$proxy is already the kotor-coop chain loader; upgrading the plugin and loader in place (chain kept)"
fi

if [ $chain = 1 ]; then
    [ -f "$package/chain-loader/dinput8.dll" ] || { echo "chain-loader/dinput8.dll not found in $package - this package has no chain loader" >&2; exit 1; }
    if [ "$existing" = foreign ]; then
        if [ -f "$previous" ]; then echo "$previous already exists - resolve that first (an earlier chain install?)" >&2; exit 1; fi
        mv "$proxy" "$previous"; echo "kept the existing dinput8.dll as dinput8.prev.dll"
    fi
    cp -f "$package/chain-loader/dinput8.dll" "$proxy"; echo "installed loader -> $proxy"
    cp -f "$package/dinput8.dll" "$chain_plugin"; echo "installed plugin -> $chain_plugin"

    entries=()
    while read -r e; do
        [ "$(echo "$e" | tr '[:upper:]' '[:lower:]')" = 'kotorcoop.plugin.dll' ] && continue
        entries+=("$e")
    done < <(chain_entries)
    entries=('KotorCoop.Plugin.dll' ${entries[@]+"${entries[@]}"})
    if [ -f "$previous" ]; then
        have=0
        for e in "${entries[@]}"; do [ "$(echo "$e" | tr '[:upper:]' '[:lower:]')" = 'dinput8.prev.dll' ] && have=1; done
        [ $have = 0 ] && entries=("${entries[0]}" 'dinput8.prev.dll' "${entries[@]:1}")
    fi
    {
        echo '# dinput8.chain - DLLs the kotor-coop loader (dinput8.dll) loads, in order.'
        echo '# One path per line, relative to this directory. # and ; start a comment.'
        echo '# The first entry that exports DirectInput8Create receives the game'"'"'s DirectInput'
        echo '# calls; the kotor-coop plugin forwards them to the entry after itself, then to'
        echo '# the system dinput8.dll. See README.txt in the package.'
        for e in "${entries[@]}"; do [ -n "$e" ] && echo "$e"; done
    } > "$chain_file"
    echo "wrote $chain_file ($(printf '%s, ' "${entries[@]}" | sed 's/, $//'))"

    asi=()
    for d in "$game" "$game/scripts" "$game/plugins"; do
        for f in "$d"/*.asi; do [ -f "$f" ] && asi+=("${f#"$game/"}"); done
    done
    if [ ${#asi[@]} -gt 0 ]; then
        echo "found ${#asi[@]} .asi mod(s) ($(printf '%s, ' "${asi[@]}" | sed 's/, $//')) - they will keep loading through ${chained_name:-the chain}"
    fi
else
    for stale in "$chain_plugin" "$chain_file"; do
        [ -f "$stale" ] && { rm -f "$stale"; echo "removed stale chain-mode file $stale"; }
    done
    cp -f "$package/dinput8.dll" "$proxy"; echo "installed -> $proxy"
    [ -f "$previous" ] && echo "note: $previous exists but nothing chains to it - run install --chain to load it alongside kotor-coop"
fi

n=0
for f in "$package"/KotorCoop.*; do
    [ -f "$f" ] || continue
    cp -f "$f" "$game/$(basename "$f")"; n=$((n + 1))
done
echo "installed agent -> $game ($n files)"

if [ -d "$package/Override" ]; then
    mkdir -p "$game/Override"
    n=0
    for f in "$package"/Override/k_coop_*; do
        [ -f "$f" ] || continue
        cp -f "$f" "$game/Override/$(basename "$f")"; n=$((n + 1))
    done
    echo "installed override assets -> $game/Override ($n files)"
fi

if [ -f "$game/coop.ini" ]; then
    echo "coop.ini already exists at $game/coop.ini - left untouched"
elif [ -f "$package/coop.ini" ]; then
    cp "$package/coop.ini" "$game/coop.ini"; echo "installed -> $game/coop.ini"
fi

echo
echo "checking the install..."
check || exit 1

# --- the one step only the player can do: the Wine DLL override ------------------------------
echo
echo "one more step - tell Wine to load the mod's dinput8.dll instead of its own:"
case "$game" in
    */steamapps/common/*)
        echo "  Steam: right-click the game -> Properties -> Launch Options:"
        echo '    WINEDLLOVERRIDES="dinput8=n,b" %command%' ;;
    */Heroic/*)
        echo "  Heroic: game settings -> Advanced -> Environment variables:"
        echo '    WINEDLLOVERRIDES = dinput8=n,b' ;;
    *)
        echo "  Steam: launch options   WINEDLLOVERRIDES=\"dinput8=n,b\" %command%"
        echo "  Heroic: environment variable WINEDLLOVERRIDES=dinput8=n,b"
        echo "  Lutris: Configure -> Runner options -> DLL overrides: dinput8 -> n,b" ;;
esac
echo "Then start the game; kotorcoop-plugin.log appears beside swkotor.exe when it worked."
