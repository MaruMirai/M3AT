#!/usr/bin/env bash
# Builds the chibi's hair from a folder of hairstyle sprites with archive/hair.py, fitted to Models/pawnHumanRig.blend.
#   ./export_hair.sh <Hairs texture folder> <out folder> [styles...]
# For hand-tuning or checking styles: the game builds any hairstyle it has no file for by itself (HairBuilder.cs). The
# output is made from that mod's art, so don't ship it in this mod; put it in a M3AThair folder at the root of a mod
# of your own to use it in place of the game's build. Style names (Bob Royalty/Anne) do just those; none does them all.
# hair.py fits the sprites to Facial Animation's head (HEAD_SPRITES), as hair drawn for FA heads is; the game's build
# fits modded hair to the vanilla head.
set -euo pipefail

BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender
if [ $# -lt 2 ]; then
    echo "usage: $0 <Hairs texture folder> <out folder> [styles...]" >&2
    exit 2
fi
HAIRS=$(realpath "$1")
OUT=$(realpath -m "$2")
shift 2
cd "$(dirname "$0")/.."

"$BLENDER" -b Models/pawnHumanRig.blend --python-exit-code 1 --python archive/hair.py -- "$HAIRS" "$OUT" "$@" \
    | grep -E "^HAIR|Error|rror:"
