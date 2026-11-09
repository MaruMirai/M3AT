#!/usr/bin/env bash
# Opens a copy of Models/pawnHumanRig.blend with the pose preview (anim_scripts/preview.py) and hairstyles put on its
# head (anim_scripts/view_hair.py), to look at them posed by any clip. The copy is thrown away.
#
# A style named is built from its sprites now, outside the game, by the mod's own HairBuilder.cs
# (anim_scripts/hairbuild/run.sh), so a change to the builder can be looked at without starting the game: Bob,
# Hair/AFUf044 (as the game's cache names it), a texPath, or a sprite path less _south.png. The sprites are the ones
# the game would use (an active retexture mod's over the game's own). It is cut down as the game cuts built hair, to
# the mod options' default hair detail (0.5); --detail sets another share, and --full shows it uncut.
# A <style>.txt path is put on as it is. With --cached, a name is looked up in the game's newest hair cache instead.
#   ./view_hair.sh [--cached] [--beard] [--detail share] [--full] Bob [Hair/AFUf044 ...]
# --keep <folder> builds into that folder (kept) instead of a temporary one.
set -euo pipefail

BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender
CACHE="$HOME/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/M3AT/hair"
cd "$(dirname "$0")"

cached=0 ; full=0 ; keep="" ; build_flags=() ; names=() ; files=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --cached) cached=1 ;;
        --full) full=1 ;;
        --beard) build_flags+=(--beard) ;;
        --detail) build_flags+=(--detail "$2"); shift ;;
        --keep) keep="$2"; shift ;;
        *) if [[ -f "$1" ]]; then files+=("$(realpath "$1")"); else names+=("$1"); fi ;;
    esac
    shift
done
if [[ ${#names[@]} -eq 0 && ${#files[@]} -eq 0 ]]; then
    sed -n 2,13p "$0" >&2
    exit 1
fi

copy=$(mktemp -d)
trap 'rm -rf "$copy"' EXIT

if [[ ${#names[@]} -gt 0 ]]; then
    if [[ $cached -eq 1 ]]; then
        for style in "${names[@]}"; do
            found=$(ls -t "$CACHE"/*/"$style.txt" 2>/dev/null | head -1 || true)
            if [[ -z "$found" ]]; then
                echo "no built hair named $style in $CACHE" >&2
                exit 1
            fi
            files+=("$found")
        done
    else
        built="${keep:-$copy/hair}"
        anim_scripts/hairbuild/run.sh "$built" "${build_flags[@]}" "${names[@]}"
        for style in "${names[@]}"; do
            base="$built/$(basename "$style")"
            if [[ -f "$base.none" ]]; then
                echo "$style: nothing solid enough to build" >&2
                continue
            fi
            if [[ $full -eq 1 && -f "$base.full.txt" ]]; then
                # view_hair.py takes <name>.png and <name>.shell.txt beside <name>.txt.
                cp "$base.png" "$base.full.png"
                files+=("$base.full.txt")
            else
                files+=("$base.txt")
            fi
        done
    fi
fi
if [[ ${#files[@]} -eq 0 ]]; then
    exit 1
fi

cp Models/pawnHumanRig.blend "$copy/view_hair.blend"
"$BLENDER" "$copy/view_hair.blend" --python anim_scripts/preview.py --python anim_scripts/view_hair.py -- "${files[@]}"
