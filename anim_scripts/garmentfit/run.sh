#!/usr/bin/env bash
# Fits garment files to the body outside the game, as the game fits another mod's garments (Source/ModGarments.cs).
#   anim_scripts/garmentfit/run.sh [--compare] [--write folder] <garment.txt> [...]
# --compare: report how far the fit is from the file's own w, key and hide lines (this mod's exported garments have
# them), to check the game's fitting against the exporter's. --write: write the fitted garments there, with this body's
# body line. Built into ~/.cache/m3at-garmentfit and run from the hair harness's folder of links to the game's install.
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
GAME="$HOME/.local/share/Steam/steamapps/common/RimWorld"
WORK="$HOME/.cache/m3at-garmentfit"
CHIBI="$ROOT/M3AT: Maru's 3D Animated Toons/Models/chibi"
if [[ -n "${M3AT_CHIBI:-}" ]]; then CHIBI="$M3AT_CHIBI"; fi

if [[ $# -lt 1 ]]; then
    sed -n 2,6p "$0" >&2
    exit 2
fi
mkdir -p "$WORK"
dotnet build "$HERE/GarmentFit.csproj" -nologo -v q -o "$WORK/bin" \
    -p:BaseIntermediateOutputPath="$WORK/obj/" -p:IntermediateOutputPath="$WORK/obj/" >"$WORK.log" 2>&1 \
    || { grep -E "error" "$WORK.log" | sort -u >&2; echo "garmentfit: the harness did not compile ($WORK.log)" >&2; exit 1; }
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
args=()
for a in "$@"; do
    if [[ -e "$a" ]]; then args+=("$(realpath "$a")"); else args+=("$a"); fi
done
cd "$run"
./RimWorld "$CHIBI" "${args[@]}"
