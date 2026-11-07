#!/usr/bin/env bash
# Re-exports the animation frames and the skinned figure (skin.txt, anim.txt) from Models/pawnHumanRig.blend into the mod's Models/chibi (MOD).
# Save the .blend in Blender first, then run: ./export_model.sh
# ./export_model.sh --fit also checks how far each garment sinks into the body in every frame (the FIT lines, a couple of
# minutes more): run it after changing a garment or the body.
set -euo pipefail

BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender
cd "$(dirname "$0")"
MOD="M3AT: Maru's 3D Animated Toons"   # the mod as the game loads it; the rest of this folder is tools

"$BLENDER" -b Models/pawnHumanRig.blend --python-exit-code 1 --python anim_scripts/export_frames.py -- "$MOD/Models/chibi" "$@" \
    | grep -E "^(SKIN|GARMENT|FIT|SOLID|SHIELD|EXPORT|WARNING)|Error|rror:"
