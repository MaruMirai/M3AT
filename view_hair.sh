#!/usr/bin/env bash
# Opens a copy of Models/pawnHumanRig.blend with the pose preview (anim_scripts/preview.py) and a hairstyle the game
# built put on its head (anim_scripts/view_hair.py), to look at it posed by any clip. A style is a <style>.txt path, or
# a name looked up in the game's newest hair cache (Hair/AFUf078 for one in a subfolder). The copy is thrown away.
#   ./view_hair.sh GreasySwoop [Fringe ...]
set -euo pipefail

BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender
CACHE="$HOME/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/M3AT/hair"
cd "$(dirname "$0")"

files=()
for style in "$@"; do
    if [[ -f "$style" ]]; then
        files+=("$(realpath "$style")")
        continue
    fi
    found=$(ls -t "$CACHE"/*/"$style.txt" 2>/dev/null | head -1 || true)
    if [[ -z "$found" ]]; then
        echo "no built hair named $style in $CACHE" >&2
        exit 1
    fi
    files+=("$found")
done
if [[ ${#files[@]} -eq 0 ]]; then
    echo "usage: $0 <style or style.txt> ..." >&2
    exit 1
fi

copy=$(mktemp -d)
trap 'rm -rf "$copy"' EXIT
cp Models/pawnHumanRig.blend "$copy/view_hair.blend"
"$BLENDER" "$copy/view_hair.blend" --python anim_scripts/preview.py --python anim_scripts/view_hair.py -- "${files[@]}"
