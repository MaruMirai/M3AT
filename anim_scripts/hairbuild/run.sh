#!/usr/bin/env bash
# Builds hairstyles (or beards) from their sprites outside the game, with the mod's own HairBuilder.cs (Main.cs).
#   anim_scripts/hairbuild/run.sh <out folder> [--beard] [--detail share] <style> [...]
# A style is as hair_sprites.py takes it (Bob, Hair/AFUf044, a texPath, or a sprite path less _south.png). Each is
# written to <out folder>/<style>.txt, .shell.txt and .png as the game writes its cache, cut down to --detail (0.5,
# the mod options' default) as the game cuts built hair, the uncut ones beside them as .full.txt. The harness is
# compiled into ~/.cache/m3at-hairbuild/bin and run from ~/.cache/m3at-hairbuild/run, a folder of links to the game's
# install with its own RimWorld.dll (and a copied launcher, as a linked one could start the real game).
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
GAME="$HOME/.local/share/Steam/steamapps/common/RimWorld"
WORK="$HOME/.cache/m3at-hairbuild"
CHIBI="$ROOT/M3AT: Maru's 3D Animated Toons/Models/chibi"
if [[ -n "${M3AT_CHIBI:-}" ]]; then CHIBI="$M3AT_CHIBI"; fi   # another chibi folder (skin.txt and anim.txt) to build on

if [[ $# -lt 2 ]]; then
    sed -n 2,7p "$0" >&2
    exit 2
fi
out=$(realpath -m "$1")
shift
beard=() ; flags=() ; styles=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --beard) beard=(--beard); flags+=(--beard) ;;
        --detail) flags+=(--detail "$2"); shift ;;
        *) styles+=("$1") ;;
    esac
    shift
done

dotnet build "$HERE/HairBuild.csproj" -nologo -v q -o "$WORK/bin" \
    -p:BaseIntermediateOutputPath="$WORK/obj/" -p:IntermediateOutputPath="$WORK/obj/" >"$WORK.log" 2>&1 \
    || { grep -E "error" "$WORK.log" | sort -u >&2; echo "hairbuild: the harness did not compile ($WORK.log)" >&2; exit 1; }

run="$WORK/run"
if [[ ! -e "$run/RimWorld.runtimeconfig.json" ]]; then
    mkdir -p "$run"
    for f in "$GAME"/*; do
        name=$(basename "$f")
        [[ "$name" == RimWorld.dll || "$name" == RimWorld ]] && continue
        ln -sfn "$f" "$run/$name"
    done
    cp "$GAME/RimWorld" "$run/RimWorld"
fi
cp "$WORK/bin/RimWorld.dll" "$run/RimWorld.dll"

sprites="$WORK/sprites"
rm -rf "$sprites"
mkdir -p "$sprites"
python3 "$ROOT/anim_scripts/hair_sprites.py" "$sprites/head" --head "${beard[@]}" >/dev/null
pairs=()
for style in "${styles[@]}"; do
    prefix="$sprites/$(echo "$style" | tr '/' '_')"
    python3 "$ROOT/anim_scripts/hair_sprites.py" "$prefix" "$style" "${beard[@]}"
    pairs+=("$(basename "$style")" "$prefix")
done
mkdir -p "$out"
cd "$run"
./RimWorld "$CHIBI" "$sprites/head" "$out" "${flags[@]}" "${pairs[@]}"
