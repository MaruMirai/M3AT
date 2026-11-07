#!/usr/bin/env bash
# Makes the body's Fat shape key by inflating it, less at the hands (anim_scripts/make_fat.py):
#   ./make_fat.sh <in.blend> <out.blend> <offset> [hands] [steps] [smooth]
# offset: how far out, in the .blend's units. hands: the share of it the hands take (default 0.4). steps, smooth: as
# inflate_garment.sh (defaults 10, 0.5). The key goes into out.blend; in.blend is never saved over.
set -euo pipefail

BLENDER=/home/t/Programs/blender-5.2.3-linux-x64/blender

if [ $# -lt 3 ]; then
    sed -n '2,5p' "$0" | sed 's/^# \?//'
    exit 1
fi

# The paths are made absolute before moving to the mod's folder, so they can be given from anywhere.
in=$(realpath "$1")
out=$(realpath -m "$2")
shift 2
cd "$(dirname "$0")"

"$BLENDER" -b "$in" --python anim_scripts/make_fat.py -- "$out" "$@"
